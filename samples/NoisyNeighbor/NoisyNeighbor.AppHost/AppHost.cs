using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

var neighborRps = builder.AddParameter("neighbor-rps", "25", publishValueAsDefault: true);
var neighborDuration = builder.AddParameter(
    "neighbor-duration",
    "150s",
    publishValueAsDefault: true
);

var grafana = builder
    .AddContainer("grafana", "grafana/otel-lgtm", "0.37.0")
    .WithHttpEndpoint(targetPort: 3000, name: "http")
    .WithEndpoint(targetPort: 4317, name: "otlp", scheme: "http")
    .WithBindMount(
        "../grafana/provisioning.yaml",
        "/otel-lgtm/grafana/conf/provisioning/dashboards/noisy-neighbor.yaml",
        isReadOnly: true
    )
    .WithBindMount("../grafana/dashboards", "/otel-lgtm/noisy-neighbor", isReadOnly: true)
    .WithUrlForEndpoint(
        "http",
        url =>
        {
            url.Url = "/d/noisy-neighbor/noisy-neighbor";
            url.DisplayText = "Noisy neighbor dashboard";
        }
    );

var otlp = grafana.GetEndpoint("otlp");

var redis = builder.AddRedis("redis");

var quotaApi = builder
    .AddProject<Projects.NoisyNeighbor_QuotaApi>("quota-api")
    .WithEnvironment("GRAFANA_OTLP_ENDPOINT", otlp)
    .WaitFor(grafana);

AddWorkers("worker", builder.Configuration.GetValue("Workers", 3));

// Aspire cannot change the replica count of a running resource. Start this one to scale out.
AddWorkers("extra-worker", 2).WithExplicitStart();

builder
    .AddContainer("neighbor", "grafana/k6", "2.3.0")
    .WithBindMount("../k6", "/scripts", isReadOnly: true)
    .WithArgs("run", "/scripts/neighbor.js")
    .WithEnvironment("QUOTA_API_URL", quotaApi.GetEndpoint("http"))
    .WithEnvironment("NEIGHBOR_RPS", neighborRps)
    .WithEnvironment("NEIGHBOR_DURATION", neighborDuration)
    .WithExplicitStart();

builder.Build().Run();

IResourceBuilder<ProjectResource> AddWorkers(string name, int replicas) =>
    builder
        .AddProject<Projects.NoisyNeighbor_Worker>(name)
        .WithReplicas(replicas)
        .WithEnvironment("GRAFANA_OTLP_ENDPOINT", otlp)
        .WithReference(quotaApi)
        .WithReference(redis)
        .WaitFor(quotaApi)
        .WaitFor(redis);
