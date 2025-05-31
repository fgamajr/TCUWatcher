using TCUWatcher.API.Models;
using MongoDB.Driver;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration; // Essencial para IConfiguration
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Options; // <<< ADICIONE ESTA LINHA

namespace TCUWatcher.API.Services;

public class MongoService : IMongoService
{
    private readonly IMongoCollection<LiveEvent> _liveEventsCollection;
    private readonly IMongoCollection<MonitoringWindow> _scheduleCollection;
    private readonly IMongoCollection<AppConfiguration> _appConfigCollection; // <<< NOVA COLEÇÃO
    private readonly ILogger<MongoService> _logger;
    private readonly MongoClient _mongoClient;

    // A classe MongoDbSettings pode ser mantida para definir padrões se alguns valores
    // não vierem da configuração ou para referência, mas não será injetada via IOptions neste exemplo.
    // Se você quiser continuar usando IOptions para os nomes de database/coleção não secretos,
    // você pode reintroduzir IOptions<MongoDbSettings> no construtor e mesclar as abordagens.
    // Por simplicidade, vamos pegar tudo de IConfiguration aqui.
    public class MongoDbSettings
    {
        public string ConnectionString { get; set; } = null!;
        public string DatabaseName { get; set; } = null!;
        public string CollectionName { get; set; } = null!;
        public string ScheduleCollectionName { get; set; } = null!;
        public string AppConfigCollectionName { get; set; } = null!; // <<< NOVA PROPRIEDADE
    }


    public MongoService(IOptions<MongoDbSettings> mongoDbSettings, ILogger<MongoService> logger, IConfiguration configuration /* if needed */)
    {
        // ... (your existing constructor logic to initialize _mongoClient, _liveEventsCollection, etc.) ...
        _logger = logger;
        try
        {
            var settings = mongoDbSettings.Value;
            _mongoClient = new MongoClient(settings.ConnectionString);
            var mongoDatabase = _mongoClient.GetDatabase(settings.DatabaseName);
            _liveEventsCollection = mongoDatabase.GetCollection<LiveEvent>(settings.CollectionName);
            _scheduleCollection = mongoDatabase.GetCollection<MonitoringWindow>(settings.ScheduleCollectionName);
            _appConfigCollection = mongoDatabase.GetCollection<AppConfiguration>(settings.AppConfigCollectionName);

            _logger.LogInformation("MongoDB client initialized. Live Events Collection: '{LiveEventsCol}'. Schedule Collection: '{ScheduleCol}'. App Config Collection: '{AppConfigCol}'.",
                                settings.CollectionName, settings.ScheduleCollectionName, settings.AppConfigCollectionName);

            // Ensure indexes after collection is initialized
            EnsureIndexesAsync().GetAwaiter().GetResult(); // Run synchronously in constructor for setup
        }
        catch (MongoConfigurationException ex)
        {
            _logger.LogCritical(ex, "Failed to initialize MongoDB client due to configuration error. Connection String: {ConnectionString}", mongoDbSettings.Value.ConnectionString);
            throw;
        }
        catch (MongoConnectionException ex)
        {
            _logger.LogCritical(ex, "Failed to connect to MongoDB server at {ConnectionString}. Please ensure the server is running and accessible.", mongoDbSettings.Value.ConnectionString);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "An unexpected error occurred during MongoDB client initialization.");
            throw;
        }
    }

    public async Task<List<LiveEvent>> GetLiveEventsAsync(FilterDefinition<LiveEvent> filter, SortDefinition<LiveEvent> sort)
    {
        try
        {
            return await _liveEventsCollection.Find(filter).Sort(sort).ToListAsync().ConfigureAwait(false);
        }
        catch (MongoException ex)
        {
            _logger.LogError(ex, "Error fetching live events from MongoDB.");
            return new List<LiveEvent>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An unexpected error occurred while fetching live events from MongoDB.");
            return new List<LiveEvent>();
        }
    }

    public async Task<LiveEvent?> GetLiveEventByVideoIdAsync(string videoId)
    {
        try
        {
            return await _liveEventsCollection.Find(x => x.VideoId == videoId).FirstOrDefaultAsync().ConfigureAwait(false);
        }
        catch (MongoException ex)
        {
            _logger.LogError(ex, "Error fetching live event by VideoId '{VideoId}' from MongoDB.", videoId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An unexpected error occurred while fetching live event by VideoId '{VideoId}' from MongoDB.", videoId);
            return null;
        }
    }

    public async Task CreateLiveEventAsync(LiveEvent liveEvent)
    {
        try
        {
            await _liveEventsCollection.InsertOneAsync(liveEvent).ConfigureAwait(false);
            _logger.LogDebug("Live event with VideoId '{VideoId}' created successfully in MongoDB.", liveEvent.VideoId);
        }
        catch (MongoWriteException ex)
        {
            if (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                _logger.LogWarning("Attempted to insert duplicate live event with VideoId '{VideoId}'. This might indicate a race condition, but is handled.", liveEvent.VideoId);
                return;
            }
            _logger.LogError(ex, "MongoDB write error creating live event with VideoId '{VideoId}'.", liveEvent.VideoId);
            throw;
        }
        catch (MongoException ex)
        {
            _logger.LogError(ex, "Generic MongoDB error creating live event with VideoId '{VideoId}'.", liveEvent.VideoId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An unexpected error occurred while creating live event with VideoId '{VideoId}' in MongoDB.", liveEvent.VideoId);
            throw;
        }
    }

    public async Task<bool> UpdateLiveEventAsync(string id, UpdateDefinition<LiveEvent> update)
    {
        try
        {
            var result = await _liveEventsCollection.UpdateOneAsync(Builders<LiveEvent>.Filter.Eq(l => l.Id, id), update).ConfigureAwait(false);
            if (result.IsAcknowledged && result.ModifiedCount > 0)
            {
                _logger.LogDebug("Live event with Id '{Id}' updated successfully in MongoDB. Modified count: {ModifiedCount}", id, result.ModifiedCount);
            }
            else if (result.IsAcknowledged && result.ModifiedCount == 0)
            {
                 _logger.LogDebug("Live event update for Id '{Id}' acknowledged but no documents were modified. The document might not exist or the update resulted in no change.", id);
            }
            return result.IsAcknowledged && result.ModifiedCount > 0;
        }
        catch (MongoException ex)
        {
            _logger.LogError(ex, "Error updating live event with Id '{Id}' in MongoDB.", id);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An unexpected error occurred while updating live event with Id '{Id}' in MongoDB.", id);
            return false;
        }
    }

    public async Task<List<LiveEvent>> FindAsync(FilterDefinition<LiveEvent> filter)
    {
        try
        {
            return await _liveEventsCollection.Find(filter).ToListAsync().ConfigureAwait(false);
        }
        catch (MongoException ex)
        {
            _logger.LogError(ex, "Error finding live events with filter from MongoDB.");
            return new List<LiveEvent>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An unexpected error occurred while finding live events with filter from MongoDB.");
            return new List<LiveEvent>();
        }
    }

    public async Task<List<MonitoringWindow>> GetActiveMonitoringWindowsAsync()
    {
        try
        {
            var filter = Builders<MonitoringWindow>.Filter.Eq(w => w.IsEnabled, true);
            return await _scheduleCollection.Find(filter).ToListAsync().ConfigureAwait(false);
        }
        catch (MongoException ex)
        {
            _logger.LogError(ex, "Error fetching active monitoring windows from MongoDB.");
            return new List<MonitoringWindow>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An unexpected error occurred while fetching active monitoring windows.");
            return new List<MonitoringWindow>();
        }
    }

    /// <inheritdoc/>
    public async Task<AppConfiguration?> GetAppConfigurationAsync()
    {
        try
        {
            // Busca o documento de configuração global pelo nome fixo
            return await _appConfigCollection.Find(c => c.ConfigName == "global_app_settings").FirstOrDefaultAsync().ConfigureAwait(false);
        }
        catch (MongoException ex)
        {
            _logger.LogError(ex, "Error fetching app configuration from MongoDB.");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An unexpected error occurred while fetching app configuration.");
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task SaveAppConfigurationAsync(AppConfiguration config)
    {
        try
        {
            // Usa ReplaceOne com IsUpsert = true para inserir se não existir, ou atualizar se existir
            await _appConfigCollection.ReplaceOneAsync(
                c => c.ConfigName == config.ConfigName,
                config,
                new ReplaceOptions { IsUpsert = true }
            ).ConfigureAwait(false);
            _logger.LogInformation("App configuration saved/updated in MongoDB.");
        }
        catch (MongoException ex)
        {
            _logger.LogError(ex, "Error saving app configuration to MongoDB.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An unexpected error occurred while saving app configuration.");
            throw;
        }
    }

    public async Task EnsureIndexesAsync()
    {
        // Index for FileHash
        var fileHashIndexKeys = Builders<LiveEvent>.IndexKeys.Ascending(x => x.FileHash);
        var fileHashIndexModel = new CreateIndexModel<LiveEvent>(
            fileHashIndexKeys, 
            new CreateIndexOptions { Name = "FileHash_Index", Sparse = true } // Sparse is good if many docs won't have FileHash
        );

        try
        {
            await _liveEventsCollection.Indexes.CreateOneAsync(fileHashIndexModel);
            _logger.LogInformation("Ensured index 'FileHash_Index' exists on 'live_events' collection for FileHash field.");
        }
        // Catch specific MongoDB command exceptions if the index already exists or has a name conflict.
        // Error code 85: IndexOptionsConflict (trying to create an index with the same key but different options/name)
        // Error code 86: IndexKeySpecsConflict (trying to create an index with the same name but different key specs)
        catch (MongoCommandException ex) when (ex.Code == 85 || ex.Code == 86 || ex.ErrorMessage.ToLowerInvariant().Contains("already exists"))
        {
            _logger.LogInformation("Index 'FileHash_Index' already exists or options conflict (which is acceptable if it serves the purpose). Details: {ErrorMessage}", ex.ErrorMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create index on FileHash for 'live_events' collection.");
            // Depending on how critical this is, you might want to throw or handle differently.
        }

        // You can add other index creations here if needed in the future.
        // Example for VideoId (ensure VideoId exists in your LiveEvent model):
        // _logger.LogInformation("Ensuring 'VideoId_Index' exists on 'live_events' collection.");
        // var videoIdIndexKeys = Builders<LiveEvent>.IndexKeys.Ascending(x => x.VideoId);
        // var videoIdIndexModel = new CreateIndexModel<LiveEvent>(
        //     videoIdIndexKeys, 
        //     new CreateIndexOptions { Name = "VideoId_Index", Unique = false } // Set Unique = true if VideoId should be unique
        // );
        // try
        // {
        //     await _liveEventsCollection.Indexes.CreateOneAsync(videoIdIndexModel);
        //     _logger.LogInformation("Ensured index 'VideoId_Index' exists on 'live_events' collection for VideoId field.");
        // }
        // catch (MongoCommandException ex) when (ex.Code == 85 || ex.Code == 86 || ex.ErrorMessage.ToLowerInvariant().Contains("already exists"))
        // {
        //     _logger.LogInformation("Index 'VideoId_Index' already exists or options conflict. Details: {ErrorMessage}", ex.ErrorMessage);
        // }
        // catch (Exception ex)
        // {
        //     _logger.LogError(ex, "Failed to create index on VideoId for 'live_events' collection.");
        // }
    }

    public async Task<LiveEvent?> GetLiveEventByFileHashAsync(string fileHash)
    {
        if (string.IsNullOrWhiteSpace(fileHash)) // Basic validation
        {
            _logger.LogWarning("Attempted to fetch live event with null or empty fileHash.");
            return null;
        }

        try
        {
            // It's good practice to ensure your LiveEvent model has an index on FileHash 
            // in MongoDB for efficient querying.
            _logger.LogDebug("Fetching live event by FileHash '{FileHash}' from MongoDB.", fileHash);
            return await _liveEventsCollection.Find(x => x.FileHash == fileHash)
                                            .FirstOrDefaultAsync()
                                            .ConfigureAwait(false);
        }
        catch (MongoException ex) // Handles exceptions specific to MongoDB operations
        {
            _logger.LogError(ex, "MongoDB error fetching live event by FileHash '{FileHash}'.", fileHash);
            return null; // Return null as the event could not be retrieved
        }
        catch (Exception ex) // Handles any other unexpected exceptions
        {
            _logger.LogError(ex, "An unexpected error occurred while fetching live event by FileHash '{FileHash}'.", fileHash);
            return null; // Return null in case of other errors
        }
    }

    public async Task<LiveEvent?> ClaimNextPendingSummaryEventAsync(FilterDefinition<LiveEvent> filter, UpdateDefinition<LiveEvent> update, FindOneAndUpdateOptions<LiveEvent, LiveEvent>? options = null)
    {
        try
        {
            return await _liveEventsCollection.FindOneAndUpdateAsync(filter, update, options);
        }
        catch (MongoException ex)
        {
            _logger.LogError(ex, "MongoDB error during ClaimNextPendingSummaryEventAsync (FindOneAndUpdate).");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during ClaimNextPendingSummaryEventAsync.");
            return null;
        }
    }

    public async Task<LiveEvent?> GetLiveEventByIdAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try
        {
            return await _liveEventsCollection.Find(x => x.Id == id).FirstOrDefaultAsync();
        }
        catch (MongoException ex)
        {
            _logger.LogError(ex, "MongoDB error fetching LiveEvent by ID '{Id}'.", id);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching LiveEvent by ID '{Id}'.", id);
            return null;
        }
    }

    public async Task<LiveEvent?> FindOneAndUpdateLiveEventAsync(FilterDefinition<LiveEvent> filter, UpdateDefinition<LiveEvent> update, FindOneAndUpdateOptions<LiveEvent, LiveEvent>? options = null)
{
    try
    {
        // If options are null, the driver will use defaults.
        // Ensure options passed from SessionSummarizationService (like ReturnDocument.After) are correctly handled.
        var findOptions = options ?? new FindOneAndUpdateOptions<LiveEvent, LiveEvent> 
        { 
            ReturnDocument = ReturnDocument.After // Default to returning the updated document if not specified
        };

        return await _liveEventsCollection.FindOneAndUpdateAsync(filter, update, findOptions);
    }
    catch (MongoException ex)
    {
        _logger.LogError(ex, "MongoDB error during FindOneAndUpdateLiveEventAsync.");
        return null;
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Unexpected error during FindOneAndUpdateLiveEventAsync.");
        return null;
    }
}
}