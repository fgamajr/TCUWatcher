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


    public MongoService(IOptions<MongoDbSettings> mongoDbSettings, ILogger<MongoService> logger)
    {
        _logger = logger;
        try
        {
            var settings = mongoDbSettings.Value;
            _mongoClient = new MongoClient(settings.ConnectionString);
            var mongoDatabase = _mongoClient.GetDatabase(settings.DatabaseName);
            _liveEventsCollection = mongoDatabase.GetCollection<LiveEvent>(settings.CollectionName);
            _scheduleCollection = mongoDatabase.GetCollection<MonitoringWindow>(settings.ScheduleCollectionName);
            _appConfigCollection = mongoDatabase.GetCollection<AppConfiguration>(settings.AppConfigCollectionName); // <<< INICIALIZA NOVA COLEÇÃO

            _logger.LogInformation("MongoDB client initialized. Live Events Collection: '{LiveEventsCol}'. Schedule Collection: '{ScheduleCol}'. App Config Collection: '{AppConfigCol}'.",
                                   settings.CollectionName, settings.ScheduleCollectionName, settings.AppConfigCollectionName);
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
}