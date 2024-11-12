using Bencodex;
using Libplanet.Crypto;
using Mimir.MongoDB;
using Mimir.MongoDB.Bson;
using Mimir.Worker.Client;
using Mimir.Worker.Initializer.Manager;
using Mimir.Worker.Services;
using Mimir.Worker.StateDocumentConverter;
using ILogger = Serilog.ILogger;

namespace Mimir.Worker.Handler;

public abstract class BaseDiffHandler(
    string collectionName,
    Address accountAddress,
    IStateDocumentConverter stateDocumentConverter,
    MongoDbService dbService,
    IStateService stateService,
    IHeadlessGQLClient headlessGqlClient,
    IInitializerManager initializerManager,
    ILogger logger
) : BackgroundService
{
    private const string PollerType = "DiffPoller";
    
    private readonly Codec _codec = new();
    protected ILogger Logger { get; } = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await initializerManager.WaitInitializers(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var (currentBaseIndex, currentTargetIndex, currentIndexOnChain, indexDifference) =
                    await CalculateCurrentAndTargetIndexes(stoppingToken);

                if (currentBaseIndex >= currentTargetIndex)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), stoppingToken);
                    continue;
                }

                Logger.Information(
                    "{CollectionName} Request diff data, current: {CurrentBlockIndex}, gap: {IndexDiff}, base: {CurrentBaseIndex} target: {CurrentTargetIndex}",
                    collectionName,
                    currentIndexOnChain,
                    indexDifference,
                    currentBaseIndex,
                    currentTargetIndex
                );

                var diffContext = await ProduceByAccount(
                    stoppingToken,
                    currentBaseIndex,
                    currentTargetIndex
                );
                await ConsumeAsync(diffContext, stoppingToken);
            }
            catch (Exception e)
            {
                Logger.Error(e, "Unexpected error occurred.");
            }
        }
    }

    private async Task<(long, long, long, long)> CalculateCurrentAndTargetIndexes(
        CancellationToken stoppingToken
    )
    {
        var syncedIndex = await GetSyncedBlockIndex(stoppingToken);
        var currentBaseIndex = syncedIndex;
        Logger.Information(
            "{CollectionName} Synced BlockIndex: {SyncedBlockIndex}",
            collectionName,
            syncedIndex
        );

        var currentIndexOnChain = await stateService.GetLatestIndex(stoppingToken, accountAddress);
        var indexDifference = currentIndexOnChain - currentBaseIndex;
        var limit =
            collectionName == CollectionNames.GetCollectionName<InventoryDocument>()
            || collectionName == CollectionNames.GetCollectionName<AvatarDocument>()
                ? 1
                : 15;
        var currentTargetIndex =
            currentBaseIndex + (indexDifference > limit ? limit : indexDifference);

        return (currentBaseIndex, currentTargetIndex, currentIndexOnChain, indexDifference);
    }

    private async Task<DiffContext> ProduceByAccount(
        CancellationToken stoppingToken,
        long currentBaseIndex,
        long currentTargetIndex
    )
    {
        var result = await headlessGqlClient.GetAccountDiffsAsync(
            currentBaseIndex,
            currentTargetIndex,
            accountAddress,
            stoppingToken
        );

        return new DiffContext
        {
            AccountAddress = accountAddress,
            CollectionName = collectionName,
            DiffResponse = result,
            TargetBlockIndex = currentTargetIndex
        };
    }

    private async Task ConsumeAsync(DiffContext diffContext, CancellationToken stoppingToken)
    {
        if (!diffContext.DiffResponse.AccountDiffs.Any())
        {
            Logger.Information("{CollectionName}: No diffs", diffContext.CollectionName);
            await dbService.UpdateLatestBlockIndexAsync(
                new MetadataDocument
                {
                    PollerType = PollerType,
                    CollectionName = diffContext.CollectionName,
                    LatestBlockIndex = diffContext.TargetBlockIndex
                }
            );
            return;
        }

        await ProcessStateDiff(
            stateDocumentConverter,
            diffContext.DiffResponse,
            diffContext.TargetBlockIndex,
            stoppingToken
        );

        await dbService.UpdateLatestBlockIndexAsync(
            new MetadataDocument
            {
                PollerType = PollerType,
                CollectionName = diffContext.CollectionName,
                LatestBlockIndex = diffContext.TargetBlockIndex
            },
            null,
            stoppingToken
        );
    }

    private async Task ProcessStateDiff(
        IStateDocumentConverter converter,
        GetAccountDiffsResponse diffResponse,
        long blockIndex,
        CancellationToken stoppingToken
    )
    {
        var documents = new List<MimirBsonDocument>();
        foreach (var diff in diffResponse.AccountDiffs)
            if (diff.ChangedState is not null)
            {
                var address = new Address(diff.Path);

                var document = converter.ConvertToDocument(
                    new AddressStatePair
                    {
                        BlockIndex = blockIndex,
                        Address = address,
                        RawState = _codec.Decode(Convert.FromHexString(diff.ChangedState))
                    }
                );

                documents.Add(document);
            }

        Logger.Information(
            "{DiffCount} Handle in {Handler} Converted {Count} States",
            diffResponse.AccountDiffs.Count(),
            converter.GetType().Name,
            documents.Count
        );

        if (documents.Count > 0)
            await dbService.UpsertStateDataManyAsync(
                collectionName,
                documents,
                null,
                stoppingToken
            );
    }

    private async Task<long> GetSyncedBlockIndex(CancellationToken stoppingToken)
    {
        try
        {
            var syncedBlockIndex = await dbService.GetLatestBlockIndexAsync(
                PollerType,
                collectionName,
                stoppingToken
            );
            return syncedBlockIndex;
        }
        catch (InvalidOperationException)
        {
            var currentBlockIndex = await stateService.GetLatestIndex(
                stoppingToken,
                accountAddress
            );
            Logger.Information(
                "Metadata collection is not found, set block index to {BlockIndex} - 1",
                currentBlockIndex
            );
            await dbService.UpdateLatestBlockIndexAsync(
                new MetadataDocument
                {
                    PollerType = PollerType,
                    CollectionName = collectionName,
                    LatestBlockIndex = currentBlockIndex - 1
                },
                cancellationToken: stoppingToken
            );
            return currentBlockIndex - 1;
        }
    }
}
