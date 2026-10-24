using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SlopGarden.Contracts;

namespace SlopGarden.Server.Data;

public class SlopDbContext(DbContextOptions<SlopDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<PersonalAccessToken> PersonalAccessTokens => Set<PersonalAccessToken>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<ServerSetting> ServerSettings => Set<ServerSetting>();
    public DbSet<Secret> Secrets => Set<Secret>();
    public DbSet<Garden> Gardens => Set<Garden>();
    public DbSet<Bed> Beds => Set<Bed>();
    public DbSet<PlantProfile> PlantProfiles => Set<PlantProfile>();
    public DbSet<Plant> Plants => Set<Plant>();
    public DbSet<CareTarget> CareTargets => Set<CareTarget>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceConfigRevision> DeviceConfigRevisions => Set<DeviceConfigRevision>();
    public DbSet<DevicePlugin> DevicePlugins => Set<DevicePlugin>();
    public DbSet<DiscoveredHardware> DiscoveredHardware => Set<DiscoveredHardware>();
    public DbSet<PluginSigningKey> PluginSigningKeys => Set<PluginSigningKey>();
    public DbSet<PluginCatalogEntry> PluginCatalog => Set<PluginCatalogEntry>();
    public DbSet<HardwareCatalogEntry> HardwareCatalog => Set<HardwareCatalogEntry>();
    public DbSet<ClientRelease> ClientReleases => Set<ClientRelease>();
    public DbSet<DeviceUpdate> DeviceUpdates => Set<DeviceUpdate>();
    public DbSet<Sensor> Sensors => Set<Sensor>();
    public DbSet<SensorChannel> SensorChannels => Set<SensorChannel>();
    public DbSet<Reading> Readings => Set<Reading>();
    public DbSet<Actuator> Actuators => Set<Actuator>();
    public DbSet<ActuatorCommand> ActuatorCommands => Set<ActuatorCommand>();
    public DbSet<ActuatorRun> ActuatorRuns => Set<ActuatorRun>();
    public DbSet<Camera> Cameras => Set<Camera>();
    public DbSet<Media> Media => Set<Media>();
    public DbSet<DeviceEvent> DeviceEvents => Set<DeviceEvent>();
    public DbSet<Schedule> Schedules => Set<Schedule>();
    public DbSet<ScheduleRun> ScheduleRuns => Set<ScheduleRun>();
    public DbSet<SafetyLimit> SafetyLimits => Set<SafetyLimit>();
    public DbSet<EmergencyStop> EmergencyStops => Set<EmergencyStop>();
    public DbSet<AiProvider> AiProviders => Set<AiProvider>();
    public DbSet<AiModelAssignment> AiModelAssignments => Set<AiModelAssignment>();
    public DbSet<ChatConversation> ChatConversations => Set<ChatConversation>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<AiProposal> AiProposals => Set<AiProposal>();
    public DbSet<AiStandingApproval> AiStandingApprovals => Set<AiStandingApproval>();
    public DbSet<AiActivity> AiActivities => Set<AiActivity>();
    public DbSet<NotificationChannel> NotificationChannels => Set<NotificationChannel>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();
    public DbSet<NotificationMute> NotificationMutes => Set<NotificationMute>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Touch();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Touch();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void Touch()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var e in ChangeTracker.Entries<ITimestamped>())
        {
            if (e.State == EntityState.Modified) e.Entity.UpdatedAt = now;
        }
    }

    /// <summary>PostgreSQL timestamptz only accepts UTC offsets; clients and cron may hand us local offsets.</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder c)
    {
        c.Properties<DateTimeOffset>().HaveConversion<UtcConverter>();
        c.Properties<DateTimeOffset?>().HaveConversion<UtcConverter>();
    }

    private sealed class UtcConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, DateTimeOffset>(
        v => v.ToUniversalTime(), v => v);

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.Username).IsUnique();
        });
        b.Entity<PersonalAccessToken>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<AuditLogEntry>(e =>
        {
            e.ToTable("audit_log");
            e.Json(x => x.Details);
            e.HasIndex(x => x.At);
        });
        b.Entity<ServerSetting>(e =>
        {
            e.HasKey(x => x.Key);
            e.JsonNullable(x => x.Value);
        });
        b.Entity<Secret>(e => e.HasIndex(x => x.Name).IsUnique());

        b.Entity<Garden>(e => e.HasMany(x => x.Beds).WithOne(x => x.Garden!).HasForeignKey(x => x.GardenId).OnDelete(DeleteBehavior.Cascade));
        b.Entity<Bed>(e => e.HasMany(x => x.Plants).WithOne(x => x.Bed!).HasForeignKey(x => x.BedId).OnDelete(DeleteBehavior.Cascade));
        b.Entity<PlantProfile>(e =>
        {
            e.HasIndex(x => x.Slug).IsUnique();
            e.Json(x => x.Sources);
        });
        b.Entity<Plant>(e => e.HasOne(x => x.PlantProfile).WithMany().HasForeignKey(x => x.PlantProfileId).OnDelete(DeleteBehavior.SetNull));
        b.Entity<CareTarget>(e =>
        {
            e.HasIndex(x => new { x.ScopeType, x.BedId, x.PlantId, x.Metric }).IsUnique().AreNullsDistinct(false);
            e.HasOne<Bed>().WithMany().HasForeignKey(x => x.BedId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Plant>().WithMany().HasForeignKey(x => x.PlantId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<JournalEntry>(e =>
        {
            e.HasIndex(x => new { x.GardenId, x.OccurredAt });
            e.HasOne<Garden>().WithMany().HasForeignKey(x => x.GardenId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Device>(e =>
        {
            e.HasIndex(x => x.HardwareId).IsUnique();
            e.Json(x => x.ConfigErrors);
            e.HasOne<Garden>().WithMany().HasForeignKey(x => x.GardenId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<DeviceConfigRevision>(e =>
        {
            e.HasIndex(x => new { x.DeviceId, x.Version }).IsUnique();
            e.Json(x => x.Document);
            e.Json(x => x.Errors);
            e.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<DevicePlugin>(e =>
        {
            e.HasIndex(x => new { x.DeviceId, x.PluginId }).IsUnique();
            e.Json(x => x.Components);
            e.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<DiscoveredHardware>(e =>
        {
            e.ToTable("discovered_hardware");
            e.HasIndex(x => new { x.DeviceId, x.PluginId, x.Address }).IsUnique();
            e.Json(x => x.SuggestedSettings);
            e.Json(x => x.Channels);
            e.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<PluginSigningKey>(e => e.HasIndex(x => x.KeyId).IsUnique());
        b.Entity<PluginCatalogEntry>(e =>
        {
            e.ToTable("plugin_catalog");
            e.HasIndex(x => new { x.PluginId, x.Version, x.Sha256 }).IsUnique();
            e.Json(x => x.SettingsSchema);
            e.HasOne(x => x.SigningKey).WithMany().HasForeignKey(x => x.SigningKeyId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<HardwareCatalogEntry>(e => e.ToTable("hardware_catalog"));

        b.Entity<Sensor>(e =>
        {
            e.Json(x => x.Settings);
            e.HasOne(x => x.Device).WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Channels).WithOne(x => x.Sensor!).HasForeignKey(x => x.SensorId).OnDelete(DeleteBehavior.Cascade);
            Targeted(e);
        });
        b.Entity<SensorChannel>(e =>
        {
            e.HasIndex(x => new { x.SensorId, x.Key }).IsUnique();
            e.JsonNullable(x => x.Calibration);
        });
        b.Entity<Reading>(e =>
        {
            e.HasKey(x => new { x.ChannelId, x.Time });
            e.HasOne<SensorChannel>().WithMany().HasForeignKey(x => x.ChannelId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Actuator>(e =>
        {
            e.Json(x => x.Settings);
            e.HasOne(x => x.Device).WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
            Targeted(e);
        });
        b.Entity<ActuatorCommand>(e =>
        {
            e.HasIndex(x => new { x.ActuatorId, x.CreatedAt });
            e.HasOne<Actuator>().WithMany().HasForeignKey(x => x.ActuatorId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ActuatorRun>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => new { x.ActuatorId, x.StartedAt });
            e.HasOne<Actuator>().WithMany().HasForeignKey(x => x.ActuatorId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Camera>(e =>
        {
            e.Json(x => x.Settings);
            e.HasOne(x => x.Device).WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
            Targeted(e);
        });
        b.Entity<Media>(e =>
        {
            e.ToTable("media");
            e.HasIndex(x => new { x.CameraId, x.CapturedAt });
        });
        b.Entity<DeviceEvent>(e =>
        {
            e.HasIndex(x => new { x.DeviceId, x.Time });
            e.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Schedule>(e =>
        {
            e.Json(x => x.Payload);
            e.HasOne<Garden>().WithMany().HasForeignKey(x => x.GardenId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Actuator>().WithMany().HasForeignKey(x => x.ActuatorId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ScheduleRun>(e =>
        {
            e.Json(x => x.Details);
            e.HasOne<Schedule>().WithMany().HasForeignKey(x => x.ScheduleId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<SafetyLimit>(e =>
        {
            e.HasIndex(x => new { x.ScopeType, x.ScopeId });
            e.HasOne<SensorChannel>().WithMany().HasForeignKey(x => x.ChannelId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<EmergencyStop>(e => e.HasIndex(x => x.Active));

        b.Entity<AiProvider>(e => e.HasOne<Secret>().WithMany().HasForeignKey(x => x.ApiKeySecretId).OnDelete(DeleteBehavior.SetNull));
        b.Entity<AiModelAssignment>(e =>
        {
            e.HasKey(x => x.Job);
            e.HasOne(x => x.Provider).WithMany().HasForeignKey(x => x.ProviderId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ChatConversation>(e =>
        {
            e.Json(x => x.LastPageContext);
            e.HasIndex(x => new { x.UserId, x.UpdatedAt });
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ChatMessage>(e =>
        {
            e.HasIndex(x => new { x.ConversationId, x.Seq }).IsUnique();
            e.JsonNullable(x => x.ToolCalls);
            e.JsonNullable(x => x.PageContext);
            e.HasOne<ChatConversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<AiProposal>(e =>
        {
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.Json(x => x.Arguments);
            e.Json(x => x.Changes);
            e.JsonNullable(x => x.Result);
        });
        b.Entity<AiStandingApproval>(e => e.Json(x => x.Constraints));
        b.Entity<AiActivity>(e => e.HasIndex(x => new { x.Status, x.StartedAt }));

        b.Entity<NotificationChannel>(e => e.Json(x => x.Settings));
        b.Entity<Notification>(e => e.HasIndex(x => x.CreatedAt));
        b.Entity<NotificationDelivery>(e => e.HasOne<Notification>().WithMany().HasForeignKey(x => x.NotificationId).OnDelete(DeleteBehavior.Cascade));
        b.Entity<NotificationMute>(e => e.HasIndex(x => new { x.ScopeType, x.ScopeId }));
    }

    private static void Targeted<T>(EntityTypeBuilder<T> e) where T : class, ITargeted
    {
        e.HasIndex(x => x.GardenId);
        e.HasOne<Garden>().WithMany().HasForeignKey(x => x.GardenId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<Bed>().WithMany().HasForeignKey(x => x.BedId).OnDelete(DeleteBehavior.SetNull);
        e.HasOne<Plant>().WithMany().HasForeignKey(x => x.PlantId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal static class JsonColumnExtensions
{
    private static readonly JsonSerializerOptions Options = SlopJson.Create();

    public static void Json<TEntity, TProp>(this EntityTypeBuilder<TEntity> e, System.Linq.Expressions.Expression<Func<TEntity, TProp>> prop)
        where TEntity : class where TProp : class
    {
        e.Property(prop)
            .HasColumnType("jsonb")
            .HasConversion(
                v => Serialize(v),
                s => Deserialize<TProp>(s)!,
                Comparer<TProp>());
    }

    public static void JsonNullable<TEntity, TProp>(this EntityTypeBuilder<TEntity> e, System.Linq.Expressions.Expression<Func<TEntity, TProp?>> prop)
        where TEntity : class where TProp : class
    {
        e.Property(prop)
            .HasColumnType("jsonb")
            .HasConversion(
                v => v == null ? null : Serialize(v),
                s => s == null ? null : Deserialize<TProp>(s),
                Comparer<TProp?>());
    }

    private static string Serialize<T>(T value) => value switch
    {
        JsonNode n => n.ToJsonString(),
        _ => JsonSerializer.Serialize(value, Options),
    };

    private static T? Deserialize<T>(string s)
    {
        if (typeof(JsonNode).IsAssignableFrom(typeof(T))) return (T?)(object?)JsonNode.Parse(s);
        return JsonSerializer.Deserialize<T>(s, Options);
    }

    private static ValueComparer<T> Comparer<T>() => new(
        (a, b) => Serialize(a) == Serialize(b),
        v => v == null ? 0 : Serialize(v).GetHashCode(),
        v => v == null ? default! : Deserialize<T>(Serialize(v))!);
}
