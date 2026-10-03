targetScope = 'subscription'

// A custom policy definition cannot live in a resource group: this file is the one piece of the
// deployment that is written at subscription scope. main.bicep assigns it to the resource group only.
param definitionName string

var app = 'Microsoft.App/containerApps'
var job = 'Microsoft.App/jobs'

resource shape 'Microsoft.Authorization/policyDefinitions@2025-03-01' = {
  name: definitionName
  properties: {
    policyType: 'Custom'
    mode: 'All'
    displayName: 'AzureBank: one small replica, manual jobs'
    description: 'Refuses a container app with more than one replica, a minimum above zero, several active revisions, plain HTTP, more than two containers, an init container or a container above half a vCPU; and a job with another trigger, parallel runs, an init container or a container above half a vCPU.'
    parameters: {
      allowedJobTriggers: {
        type: 'Array'
        defaultValue: [
          'Manual'
        ]
      }
    }
    policyRule: {
      if: {
        anyOf: [
          {
            allOf: [
              { field: 'type', equals: app }
              {
                anyOf: [
                  { field: '${app}/template.scale.maxReplicas', greater: 1 }
                  { field: '${app}/template.scale.minReplicas', greater: 0 }
                  {
                    allOf: [
                      { field: '${app}/configuration.activeRevisionsMode', exists: true }
                      { field: '${app}/configuration.activeRevisionsMode', notEquals: 'Single' }
                    ]
                  }
                  { field: '${app}/configuration.ingress.allowInsecure', equals: true }
                  { count: { field: '${app}/template.containers[*]' }, greater: 2 }
                  // An init container is a container too: none, or the two rules around this one
                  // would bound nothing.
                  { count: { field: '${app}/template.initContainers[*]' }, greater: 0 }
                  {
                    count: {
                      field: '${app}/template.containers[*]'
                      where: { field: '${app}/template.containers[*].resources.cpu', greater: json('0.5') }
                    }
                    greater: 0
                  }
                ]
              }
            ]
          }
          {
            allOf: [
              { field: 'type', equals: job }
              {
                anyOf: [
                  {
                    allOf: [
                      { field: '${job}/configuration.triggerType', exists: true }
                      { field: '${job}/configuration.triggerType', notIn: '[parameters(\'allowedJobTriggers\')]' }
                    ]
                  }
                  // One run at a time, whatever starts it: a trigger allowed later must not bring
                  // parallel runs with it.
                  { field: '${job}/configuration.manualTriggerConfig.parallelism', greater: 1 }
                  { field: '${job}/configuration.scheduleTriggerConfig.parallelism', greater: 1 }
                  { field: '${job}/configuration.eventTriggerConfig.parallelism', greater: 1 }
                  { count: { field: '${job}/template.initContainers[*]' }, greater: 0 }
                  {
                    count: {
                      field: '${job}/template.containers[*]'
                      where: { field: '${job}/template.containers[*].resources.cpu', greater: json('0.5') }
                    }
                    greater: 0
                  }
                ]
              }
            ]
          }
        ]
      }
      then: {
        effect: 'deny'
      }
    }
  }
}

output definitionId string = shape.id
