param namePrefix string
param location string
param tags object
param workspaceId string
param applicationInsightsId string
param actionGroupId string

var definitions = [
  {
    name: 'accepted-report-gap'
    description: 'No accepted (202) reports in 15 minutes, including no telemetry.'
    query: format(
      '''
      AppMetrics
      | where TimeGenerated > ago(15m)
      | where _ResourceId =~ '{0}'
      | where Name == "evaluator.publishes" and tostring(Properties["status"]) == "202"
      | summarize Accepted = sum(Sum)
      | project Accepted = coalesce(Accepted, 0.0)
      ''',
      applicationInsightsId
    )
    operator: 'LessThan'
    threshold: 1
  }
  {
    name: 'host-restart-loop'
    description: 'More than three structured evaluator host-start events in 15 minutes.'
    query: format(
      '''
      AppTraces
      | where TimeGenerated > ago(15m)
      | where _ResourceId =~ '{0}'
      | where tostring(Properties["Event"]) == "evaluator.host.starts"
      | summarize Starts = count()
      ''',
      applicationInsightsId
    )
    operator: 'GreaterThan'
    threshold: 3
  }
]
resource alerts 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = [
  for alert in definitions: {
    name: '${namePrefix}-${alert.name}'
    location: location
    tags: tags
    kind: 'LogAlert'
    properties: {
      displayName: '${namePrefix}: ${alert.description}'
      description: alert.description
      enabled: true
      severity: 2
      scopes: [workspaceId]
      evaluationFrequency: 'PT5M'
      windowSize: 'PT15M'
      autoMitigate: true
      criteria: {
        allOf: [
          {
            query: alert.query
            timeAggregation: 'Maximum'
            metricMeasureColumn: alert.name == 'accepted-report-gap' ? 'Accepted' : 'Starts'
            operator: alert.operator
            threshold: alert.threshold
            failingPeriods: {
              numberOfEvaluationPeriods: 1
              minFailingPeriodsToAlert: 1
            }
          }
        ]
      }
      actions: {
        actionGroups: [actionGroupId]
      }
    }
  }
]
