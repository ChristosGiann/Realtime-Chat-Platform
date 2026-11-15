using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Options;

namespace Chat.Api.Infrastructure;

public sealed class SharedDataProtectionOptions
{
    public string KeyRingPath { get; set; } = "";
    public string ApplicationName { get; set; } = "";
}

public sealed class RealtimeInstanceOptions
{
    // Configuration identity only: no ownership, leases or epoch runtime in #97.
    public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");
}

public static class SharedDataProtection
{
    public static IServiceCollection AddSharedDataProtection(this IServiceCollection services)
    {
        services.AddOptions<SharedDataProtectionOptions>().BindConfiguration("SharedDataProtection")
            .Validate(value => (string.IsNullOrWhiteSpace(value.KeyRingPath) && string.IsNullOrWhiteSpace(value.ApplicationName))
                || (!string.IsNullOrWhiteSpace(value.KeyRingPath) && Path.IsPathFullyQualified(value.KeyRingPath) && !string.IsNullOrWhiteSpace(value.ApplicationName)),
                "SharedDataProtection requires an absolute KeyRingPath and an ApplicationName together.")
            .ValidateOnStart();
        services.AddOptions<RealtimeInstanceOptions>().BindConfiguration("RealtimeInstance")
            .Validate(value => Redis.RedisOptions.ValidSegment(value.InstanceId), "Invalid realtime instance identity.")
            .ValidateOnStart();
        services.AddDataProtection();
        services.AddOptions<DataProtectionOptions>().Configure<IOptions<SharedDataProtectionOptions>>((options, shared) =>
        {
            if (!string.IsNullOrWhiteSpace(shared.Value.ApplicationName)) options.ApplicationDiscriminator = shared.Value.ApplicationName;
        });
        services.AddOptions<KeyManagementOptions>().Configure<IOptions<SharedDataProtectionOptions>, ILoggerFactory>((options, shared, logs) =>
        {
            if (!string.IsNullOrWhiteSpace(shared.Value.KeyRingPath))
                options.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(shared.Value.KeyRingPath), logs);
        });
        return services;
    }
}
