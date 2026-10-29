namespace SelfHostedEvaluators.Plugins.Abstractions;

/// <summary>Defines the entry point of an image-bundled sink package.</summary>
public interface IPluginPackage
{
    /// <summary>Registers exactly one sink plugin and any optional custom authentication schemes.</summary>
    /// <param name="registrar">The host registrar, which rejects duplicate sinks and scheme names.</param>
    void Register(IPluginRegistrar registrar);
}
