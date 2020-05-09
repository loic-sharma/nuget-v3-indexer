﻿using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BaGet.Protocol;
using BaGet.Protocol.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NuGet.Packaging.Core;

namespace V3Indexer
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            ThreadPool.SetMinThreads(workerThreads: 32, completionPortThreads: 4);
            ServicePointManager.DefaultConnectionLimit = 32;
            ServicePointManager.MaxServicePointIdleTime = 10000;

            var hostBuilder = Host.CreateDefaultBuilder(args);

            try
            {
                await hostBuilder
                    .ConfigureServices(ConfigureService)
                    .RunConsoleAsync();
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static void ConfigureService(IServiceCollection services)
        {
            services
                .AddHttpClient("NuGet")
                .ConfigurePrimaryHttpMessageHandler(handler =>
                {
                    return new HttpClientHandler
                    {
                        AutomaticDecompression = DecompressionMethods.Deflate | DecompressionMethods.GZip
                    };
                });

            services.AddSingleton(provider =>
            {
                var factory = provider.GetRequiredService<IHttpClientFactory>();
                var httpClient = factory.CreateClient("NuGet");

                var serviceIndex = "https://api.nuget.org/v3/index.json";

                return new NuGetClientFactory(httpClient, serviceIndex);
            });

            services.AddHostedService<ProcessCatalogService>();
        }
    }

    public class ProcessCatalogService : BackgroundService
    {
        private readonly NuGetClientFactory _factory;
        private readonly ILogger<ProcessCatalogService> _logger;

        public ProcessCatalogService(
            NuGetClientFactory factory,
            ILogger<ProcessCatalogService> logger)
        {
            _factory = factory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            var cursor = DateTimeOffset.MinValue;

            while (!cancellationToken.IsCancellationRequested)
            {
                var stopwatch = Stopwatch.StartNew();
                var queue = Channel.CreateBounded<string>(
                    new BoundedChannelOptions(capacity: 128)
                    {
                        FullMode = BoundedChannelFullMode.Wait,
                        SingleWriter = false,
                        SingleReader = false,
                    });

                var producer = ProduceWorkAsync(cursor, queue.Writer, cancellationToken);
                var consumer = ConsumeWorkAsync(queue.Reader, cancellationToken);

                await Task.WhenAll(producer, consumer);

                cursor = producer.Result;
                _logger.LogInformation(
                    "Processed catalog up to {Cursor} in {DurationMinutes} minutes.",
                    cursor,
                    stopwatch.Elapsed.TotalMinutes);

                _logger.LogInformation("Sleeping...");
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }
        }

        private async Task<DateTimeOffset> ProduceWorkAsync(
            DateTimeOffset minCursor,
            ChannelWriter<string> queue,
            CancellationToken cancellationToken)
        {
            await Task.Yield();

            _logger.LogInformation("Fetching catalog index...");
            var client = _factory.CreateCatalogClient();
            var catalogIndex = await client.GetIndexAsync(cancellationToken);

            var maxCursor = catalogIndex.CommitTimestamp;
            var pages = catalogIndex.GetPagesInBounds(minCursor, maxCursor);

            if (!pages.Any() || minCursor == maxCursor)
            {
                _logger.LogInformation("No pending work on the catalog.");
                queue.Complete();

                return maxCursor;
            }

            var work = new ConcurrentBag<CatalogPageItem>(pages);
            var enqueued = new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            _logger.LogInformation(
                "Fetching {Pages} catalog pages from time {MinCursor} to {MaxCursor}...",
                pages.Count,
                minCursor,
                maxCursor);

            var producerTasks = Enumerable
                .Repeat(0, Math.Min(16, pages.Count))
                .Select(async _ =>
                {
                    await Task.Yield();

                    while (work.TryTake(out var pageItem))
                    {
                        var done = false;
                        while (!done)
                        {
                            try
                            {
                                //_logger.LogInformation("Processing catalog page {PageUrl}...", pageItem.CatalogPageUrl);
                                var page = await client.GetPageAsync(pageItem.CatalogPageUrl, cancellationToken);

                                foreach (var leaf in page.Items)
                                {
                                    // Don't process leaves that are not within the cursors.
                                    if (leaf.CommitTimestamp <= minCursor) continue;
                                    if (leaf.CommitTimestamp > maxCursor) continue;

                                    // Don't reprocess an ID we've already seen.
                                    if (!enqueued.TryAdd(leaf.PackageId, value: null)) continue;

                                    if (!queue.TryWrite(leaf.PackageId))
                                    {
                                        await queue.WriteAsync(leaf.PackageId, cancellationToken);
                                    }
                                }

                                done = true;
                            }
                            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
                            {
                                _logger.LogError(e, "Retrying catalog page {PageUrl} in 5 seconds...", pageItem.CatalogPageUrl);
                                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                            }
                        }
                    }
                });

            await Task.WhenAll(producerTasks);

            _logger.LogInformation("Fetched catalog pages up to cursor {Cursor}", maxCursor);
            queue.Complete();

            return maxCursor;
        }

        private async Task ConsumeWorkAsync(ChannelReader<string> queue, CancellationToken cancellationToken)
        {
            var client = _factory.CreatePackageMetadataClient();

            _logger.LogInformation("Processing packages...");

            var consumerTasks = Enumerable
                .Repeat(0, 32)
                .Select(async _ =>
                {
                    await Task.Yield();

                    while (await queue.WaitToReadAsync(cancellationToken))
                    {
                        while (queue.TryRead(out var packageId))
                        {
                            var index = await client.GetRegistrationIndexOrNullAsync(packageId, cancellationToken);
                            if (index == null)
                            {
                                _logger.LogWarning("Package {PackageId} has been deleted.", packageId);
                                continue;
                            }

                            foreach (var pageItem in index.Pages)
                            {
                                if (pageItem.ItemsOrNull == null)
                                {
                                    var page = await client.GetRegistrationPageAsync(pageItem.RegistrationPageUrl);
                                }
                            }
                        }
                    }
                });

            await Task.WhenAll(consumerTasks);

            _logger.LogInformation("Done processing packages.");
        }
    }

    public static class NuGetExtensions
    {
        public static PackageIdentity ParsePackageIdentity(this CatalogLeafItem leaf)
        {
            return new PackageIdentity(leaf.PackageId, leaf.ParsePackageVersion());
        }
    }
}
