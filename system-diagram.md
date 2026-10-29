# Self-Hosted Evaluator System Diagram

This diagram shows the system as deployed:

- the Azure resources created by Bicep
- the components inside the container
- the plugin packages and the sink clients built from them
- the external systems it talks to

Numbers in brackets refer to the steps under [Runtime flow](#runtime-flow).

Required environment variables and JSON examples are documented in the
[configuration guide](docs/configuration.md).

```mermaid
---
config:
  theme: base
  themeVariables:
    darkMode: false
    textColor: "#1b1b1b"
    primaryTextColor: "#1b1b1b"
    titleColor: "#1b1b1b"
    lineColor: "#605e5c"
    edgeLabelBackground: "#e2e8f0"
  elk:
    lineHops: false
---
flowchart TB
    subgraph workstation["Operator workstation: PowerShell 7"]
        sValPkg["validate-package.ps1<br/>runs validation tests<br/>against a user built package"]
        sBldImg["build-image.ps1<br/>includes selected plugins"]
        sValCfg["validate-config.ps1<br/>runs validation tests<br/>against the deployment image and config"]
        sDeploy["deploy.ps1"]
    end

    bicep["infra/main.bicep<br/>defines everything<br/>in the resource group"]
    demo(["Demo system, out of scope<br/>creates models and entities"])

    subgraph rg["Resource group"]
        acr["Container Registry<br/>host and bundled plugins<br/>image pinned by digest"]
        uami["User-assigned managed identity<br/>runtime auth and image pull"]
        kv["Key Vault<br/>sink secrets and certificates,<br/>set by admins"]
        subgraph app["ACI container, Always restart, no public ports"]
            cfg["Environment variables<br/>SHE_PLUGIN_{KEY}<br/>SHE_MODEL_{KEY}<br/>AZURE_CLIENT_ID<br/>APPLICATIONINSIGHTS_CONNECTION_STRING"]
            cs["Config Store"]
            pm["Package Manager"]
            auth["Secret Resolver<br/>and Auth Registry"]
            sched["Scheduler"]
            poll["Poll Executor"]
            subgraph plugins["Packages"]
                subgraph pkDd["Datadog package"]
                    cliDdEu["datadog-eu-prod<br/>plugin"]
                    cliDdUs["datadog-us-prod<br/>plugin"]
                end
            end
            eval["Evaluator"]
            rb["Report Builder"]
            pub["Publisher"]
        end
        mon["Application Insights<br/>and Log Analytics<br/>OpenTelemetry, optional alerts"]
        net["Networking, optional<br/>ACI delegated subnet and NAT<br/>private: separate endpoints<br/>for Key Vault and ACR"]
    end

    subgraph sinks["External sinks"]
        ddEu["Datadog EU site"]
        ddUs["Datadog US site"]
    end

    subgraph hm["Azure Monitor Health Models"]
        dp["Data plane, one endpoint per model<br/>https://{model-id}.{regional-zone}"]
        hmodel["Existing health models<br/>entities and signals"]
    end

    sValPkg ==>|"validated package"| sBldImg
    sBldImg ==>|"built image"| sValCfg    
    sValCfg ==>|"validated image"| sDeploy
    sDeploy ==>|"deploy"| bicep
    bicep ==> |"push image"| acr    
    bicep ==>|"injects environment"| cfg
    
    demo -.->|"creates"| hmodel

    acr --> app
    uami -.-> app
    uami -.->|"Contributor (model-scoped)"| hmodel

    cfg -->|"(1) startup snapshot"| cs
    kv -->|"(3) kv:// secrets"| auth
    cs -->|"(2) load packages"| pm
    cs -->|"model entries"| sched
    auth -.->|"auth handlers"| pm
    pm -->|"load plugins"| plugins
    sched -->|"(4) group ticks"| poll
    poll -->|"(5) batches"| plugins
    cliDdEu ----->|"(6) HTTPS"| ddEu
    cliDdUs ----->|"(6) HTTPS"| ddUs
    net -.-> sinks
    poll -->|"(7) results"| eval
    eval -->|"health states"| rb
    rb -->|"report bodies"| pub
    auth -.->|"data-plane tokens"| pub
    pub -->|"(8) POST healthReports"| dp
    dp -->|"updates signals"| hmodel
    app --> mon

    linkStyle default color:#111827,font-weight:600

    classDef azure fill:#e8f1fb,stroke:#0078d4,color:#1b1b1b
    classDef core fill:#f3f2f1,stroke:#605e5c,color:#1b1b1b
    classDef plugin fill:#e9f5e9,stroke:#107c10,color:#1b1b1b
    classDef external fill:#fdf1e6,stroke:#ca5010,color:#1b1b1b
    classDef tool fill:#ffffff,stroke:#605e5c,color:#1b1b1b
    classDef outofscope fill:#ffffff,stroke:#8a8886,stroke-dasharray:5 5,color:#1b1b1b
    class acr,uami,kv,mon,net,dp,hmodel azure
    class cfg,cs,pm,auth,sched,poll,eval,rb,pub core
    class cliDdEu,cliDdUs plugin
    class ddEu,ddUs external
    class sValPkg,sBldImg,sValCfg,sDeploy,bicep tool
    class demo outofscope
    style workstation fill:#faf9f8,stroke:#8a8886
    style rg fill:#f7fafd,stroke:#0078d4
    style app fill:#ffffff,stroke:#605e5c
    style plugins fill:#f6fbf6,stroke:#107c10
    style pkDd fill:#ffffff,stroke:#107c10
    style sinks fill:#fffaf6,stroke:#ca5010
    style hm fill:#f7fafd,stroke:#0078d4
```

## Runtime flow

1. **Config.** Bicep injects one JSON environment entry per sink and per model. The Config Store validates the complete environment once at startup and builds an immutable snapshot. Invalid configuration fails startup; no storage account or hot-reload source is used.
2. **Packages.** The Plugin Manager loads assemblies and manifests already bundled under `/app/packages` in the image, with one `AssemblyLoadContext` per package. Each plugin entry creates a sink client with auth-configured `HttpClient`s; plugins never see raw credentials. No runtime package download is required.
3. **Secrets.** The Secret Resolver resolves `kv://` references from Key Vault. The Auth Registry builds auth handlers for the sink clients and data-plane tokens for the Publisher, using those secrets where a profile needs them.
4. **Scheduling.** The Scheduler groups signals by (plugin, interval) and fires aligned ticks.
5. **Polling.** The Poll Executor splits each group into batches and sends them to the plugin's sink client. It applies the plugin's batch size, rate limit, concurrency and timeout.
6. **Querying.** Each sink client queries its own sink over HTTPS.
7. **Evaluation.** The Evaluator turns the results into health states. Failures become `Unknown`. The Report Builder then builds the report bodies.
8. **Publishing.** The Publisher sends each report to its model's data plane, with rate limiting, retries and superseding for each model. It authenticates as the identity set in the model's `healthModel.auth`, which defaults to the managed identity.

Plugin names such as `datadog-eu-prod`, `datadog-us-prod` are examples. Each has its own `SHE_PLUGIN_<KEY>` JSON entry referencing a shared image-bundled package.
