using CoreIns.Modules.Reinsurance;
using CoreIns.Modules.Reinsurance.Registry;
using Hangfire;
using Hangfire.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoreIns.IntegrationTests.Reinsurance.Registry;

/// <summary>The recurring scanner is registered with Hangfire in the worker only (D-ARC-04, REQ-RI-058).</summary>
public sealed class LifecycleJobTests
{
    private sealed class RecordingJobs : IRecurringJobManager
    {
        public List<(string Id, Job Job, string Cron)> Added { get; } = [];

        public void AddOrUpdate(string recurringJobId, Job job, string cronExpression) => Added.Add((recurringJobId, job, cronExpression));

        public void AddOrUpdate(string recurringJobId, Job job, string cronExpression, RecurringJobOptions options) => Added.Add((recurringJobId, job, cronExpression));

        public void Trigger(string recurringJobId) => throw new NotSupportedException();

        public void RemoveIfExists(string recurringJobId) => throw new NotSupportedException();
    }

    [Fact]
    public async Task REQ_RI_058_The_worker_registers_one_recurring_scan_of_the_lifecycle_job()
    {
        var jobs = new RecordingJobs();
        var registrar = new LifecycleJobRegistrar(jobs, Options.Create(new ReinsuranceOptions { ScannerIntervalMinutes = 5 }), NullLogger<LifecycleJobRegistrar>.Instance);
        await registrar.StartAsync(TestContext.Current.CancellationToken);

        var (id, job, cron) = jobs.Added.Single();
        id.ShouldBe("ri-contract-lifecycle");
        cron.ShouldBe("*/5 * * * *");
        job.Type.ShouldBe(typeof(ReinsuranceLifecycleJob));
        job.Method.Name.ShouldBe(nameof(ReinsuranceLifecycleJob.RunAsync));
    }

    [Theory]
    [InlineData("worker", true)]
    [InlineData("WORKER", true)]
    [InlineData("api", false)]
    [InlineData("migrate", false)]
    [InlineData(null, false)]
    public void The_scanner_is_hosted_only_by_the_worker(string? role, bool registered)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(role is null ? [] : new Dictionary<string, string?> { ["APP_ROLE"] = role }).Build();
        var services = new ServiceCollection().AddReinsuranceModule(configuration);

        services.Any(d => d.ImplementationType == typeof(LifecycleJobRegistrar)).ShouldBe(registered);
        services.Any(d => d.ServiceType == typeof(ReinsuranceLifecycleJob)).ShouldBeTrue();
    }
}
