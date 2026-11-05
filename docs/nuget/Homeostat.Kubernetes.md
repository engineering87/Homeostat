# Homeostat.Kubernetes

Kubernetes actions for Homeostat: restart the application's own pod, rollout restart and scale a deployment, cordon the node. It talks to the API server directly with the pod's service account (no client SDK), and every action requires a person's approval by default.

## Install

```bash
dotnet add package Homeostat.Kubernetes --prerelease
```

The current version is a release candidate (`1.0.0-rc.1`): `--prerelease` is needed until 1.0.0 is published.

## Example

```csharp
using Homeostat;

builder.Services.AddHomeostat(h => h
    .UseKubernetes()                                                 // in-cluster defaults
    .AddThreadPoolStarvationDetector()
    .AddCondition("pod-degraded", c => c
        .ForSource("threadpool")
        .SustainedFor(TimeSpan.FromMinutes(2))
        .Heal(p => p
            .ScaleDeployment("weather-api", by: 2, maximum: 10)     // with approval
            .RestartPod())));                                        // last resort, with approval
```

Grant the service account only the permissions of the actions you use: see the RBAC example in the documentation.

## Documentation

[Kubernetes](https://github.com/engineering87/Homeostat/blob/main/docs/cloud.md#kubernetes)

This package extends [Homeostat](https://www.nuget.org/packages/Homeostat), the self-healing engine for .NET: conditions, healing plans, verification and guardrails. Start with its [getting started guide](https://github.com/engineering87/Homeostat/blob/main/docs/getting-started.md).

MIT licensed.
