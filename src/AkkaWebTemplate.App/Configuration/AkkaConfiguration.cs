﻿using System.Diagnostics;
using Akka.Actor;
using Akka.Cluster.Hosting;
using Akka.Cluster.Sharding;
using Akka.Configuration;
using Akka.Discovery.Azure;
using Akka.Discovery.Config.Hosting;
using Akka.Hosting;
using Akka.Management;
using Akka.Management.Cluster.Bootstrap;
using Akka.Persistence.Azure;
using Akka.Persistence.Azure.Hosting;
using Akka.Persistence.Hosting;
using Akka.Remote.Hosting;
using Akka.Util;
using AkkaWebTemplate.App.Actors;
using AkkaWebTemplate.Domain;

namespace AkkaWebTemplate.App.Configuration;

public static class AkkaConfiguration
{
    public static IServiceCollection ConfigureWebApiAkka(this IServiceCollection services, IConfiguration configuration,
        Action<AkkaConfigurationBuilder, IServiceProvider> additionalConfig)
    {
        var akkaSettings = configuration.GetRequiredSection("AkkaSettings").Get<AkkaSettings>();
        Debug.Assert(akkaSettings != null, nameof(akkaSettings) + " != null");

        services.AddSingleton(akkaSettings);

        return services.AddAkka(akkaSettings.ActorSystemName, (builder, sp) =>
        {
            builder.ConfigureActorSystem(sp);
            additionalConfig(builder, sp);
        });
    }

    public static AkkaConfigurationBuilder ConfigureActorSystem(this AkkaConfigurationBuilder builder,
        IServiceProvider sp)
    {
        var settings = sp.GetRequiredService<AkkaSettings>();

        return builder
            .ConfigureLoggers(configBuilder =>
            {
                configBuilder.LogConfigOnStart = settings.LogConfigOnStart;
                configBuilder.AddLoggerFactory();
            })
            .ConfigureNetwork(sp)
            .ConfigurePersistence(sp)
            .ConfigureCounterActors(sp);
    }

    public static AkkaConfigurationBuilder ConfigureNetwork(this AkkaConfigurationBuilder builder,
        IServiceProvider serviceProvider)
    {
        var settings = serviceProvider.GetRequiredService<AkkaSettings>();
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();

        if (!settings.UseClustering)
            return builder;

        var b = builder
            .WithRemoting(settings.RemoteOptions);

        if (settings.AkkaManagementOptions is { Enabled: true })
        {
            // need to delete seed-nodes so Akka.Management will take precedence
            var clusterOptions = settings.ClusterOptions;
            clusterOptions.SeedNodes = Array.Empty<string>();

            b = b
                .WithClustering(clusterOptions)
                .WithAkkaManagement(hostName: settings.AkkaManagementOptions.Hostname,
                    settings.AkkaManagementOptions.Port)
                .WithClusterBootstrap(serviceName: settings.AkkaManagementOptions.ServiceName,
                    portName: settings.AkkaManagementOptions.PortName,
                    requiredContactPoints: settings.AkkaManagementOptions.RequiredContactPointsNr);

            switch (settings.AkkaManagementOptions.DiscoveryMethod)
            {
                case DiscoveryMethod.Kubernetes:
                    break;
                case DiscoveryMethod.AwsEcsTagBased:
                    break;
                case DiscoveryMethod.AwsEc2TagBased:
                    break;
                case DiscoveryMethod.AzureTableStorage:
                    {
                        var connectionStringName = configuration.GetSection("AzureStorageSettings")
                            .Get<AzureStorageSettings>()?.ConnectionStringName;
                        Debug.Assert(connectionStringName != null, nameof(connectionStringName) + " != null");
                        var connectionString = configuration.GetConnectionString(connectionStringName);

                        b = b.WithAzureDiscovery(options =>
                        {
                            options.ServiceName = settings.AkkaManagementOptions.ServiceName;
                            options.ConnectionString = connectionString;
                        });
                        break;
                    }
                case DiscoveryMethod.Config:
                    {
                        b = b
                            .WithConfigDiscovery(options =>
                            {
                                options.Services.Add(new Service
                                {
                                    Name = settings.AkkaManagementOptions.ServiceName,
                                    Endpoints = new[]
                                    {
                                    $"{settings.AkkaManagementOptions.Hostname}:{settings.AkkaManagementOptions.Port}",
                                    }
                                });
                            });
                        break;
                    }
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
        else
        {
            b = b.WithClustering(settings.ClusterOptions);
        }

        return b;
    }

    public static Config GetPersistenceHocon(string configurationString = "localhost:6379") => $@"
        akka {{
            persistence {{
                journal {{
                    plugin = ""akka.persistence.journal.redis""
                    redis {{
                        class = ""Akka.Persistence.Redis.Journal.RedisJournal, Akka.Persistence.Redis""
                        plugin-dispatcher = ""akka.actor.default-dispatcher""
                        configuration-string = ""{configurationString}""
                    }}
                }}
                snapshot-store {{
                    plugin = ""akka.persistence.snapshot-store.redis""
                    redis {{
                        class = ""Akka.Persistence.Redis.Snapshot.RedisSnapshotStore, Akka.Persistence.Redis""
                        plugin-dispatcher = ""akka.actor.default-dispatcher""
                        configuration-string = ""{configurationString}""
                    }}
                }}
            }}
        }}
    ";

    public static AkkaConfigurationBuilder ConfigurePersistence(this AkkaConfigurationBuilder builder,
        IServiceProvider serviceProvider)
    {
        var settings = serviceProvider.GetRequiredService<AkkaSettings>();
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();

        switch (settings.PersistenceMode)
        {
            case PersistenceMode.InMemory:
                return builder.WithInMemoryJournal().WithInMemorySnapshotStore();
            case PersistenceMode.Azure:
            {
                var connectionStringName = configuration.GetSection("AzureStorageSettings")
                .Get<AzureStorageSettings>()?.ConnectionStringName;
                Debug.Assert(connectionStringName != null, nameof(connectionStringName) + " != null");
                var connectionString = configuration.GetConnectionString(connectionStringName);
                Debug.Assert(connectionString != null, nameof(connectionString) + " != null");

                return builder.WithAzurePersistence(connectionString);
            }

            case PersistenceMode.Redis:
                return builder.AddHocon(
                       GetPersistenceHocon().WithFallback(AzurePersistence.DefaultConfig),
                       HoconAddMode.Append);
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    public static AkkaConfigurationBuilder ConfigureCounterActors(this AkkaConfigurationBuilder builder,
        IServiceProvider serviceProvider)
    {
        var settings = serviceProvider.GetRequiredService<AkkaSettings>();
        var extractor = CreateCounterMessageRouter();

        if (settings.UseClustering)
        {
            return builder.WithShardRegion<CounterActor>("counter",
                (system, registry, resolver) => s => Props.Create(() => new CounterActor(s)),
                extractor, settings.ShardOptions);
        }
        else
        {
            return builder.WithActors((system, registry, resolver) =>
            {
                var parent =
                    system.ActorOf(
                        GenericChildPerEntityParent.Props(extractor, s => Props.Create(() => new CounterActor(s))),
                        "counters");
                registry.Register<CounterActor>(parent);
            });
        }
    }

    public static HashCodeMessageExtractor CreateCounterMessageRouter()
    {
        var extractor = HashCodeMessageExtractor.Create(30, o =>
        {
            return o switch
            {
                IWithCounterId counterId => counterId.CounterId,
                ShardRegion.StartEntity startEntity => startEntity.EntityId,
                _ => string.Empty
            };
        }, o => o);
        return extractor;
    }
}