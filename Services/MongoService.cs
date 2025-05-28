using TCUWatcher.API.Models;
using MongoDB.Driver;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration; // Essencial para IConfiguration
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TCUWatcher.API.Services;

public class MongoService : IMongoService
{
    private readonly IMongoCollection<LiveEvent> _liveEventsCollection;
    private readonly IMongoCollection<MonitoringWindow> _scheduleCollection;
    private readonly ILogger<MongoService> _logger;
    private readonly MongoClient _mongoClient;

    // A classe MongoDbSettings pode ser mantida para definir padrões se alguns valores
    // não vierem da configuração ou para referência, mas não será injetada via IOptions neste exemplo.
    // Se você quiser continuar usando IOptions para os nomes de database/coleção não secretos,
    // você pode reintroduzir IOptions<MongoDbSettings> no construtor e mesclar as abordagens.
    // Por simplicidade, vamos pegar tudo de IConfiguration aqui.
    private const string DefaultDatabaseName = "tcu_monitor";
    private const string DefaultLiveEventsCollectionName = "live_events";
    private const string DefaultScheduleCollectionName = "monitoring_schedule";


    public MongoService(ILogger<MongoService> logger, IConfiguration configuration)
    {
        _logger = logger;

        string? connectionString = configuration["MongoDb:ConnectionString"];
        string databaseName = configuration.GetValue<string>("MongoDb:DatabaseName") ?? DefaultDatabaseName;
        string liveEventsCollectionName = configuration.GetValue<string>("MongoDb:CollectionName") ?? DefaultLiveEventsCollectionName;
        string scheduleCollectionName = configuration.GetValue<string>("MongoDb:ScheduleCollectionName") ?? DefaultScheduleCollectionName;

        _logger.LogInformation("MongoService Initializing with Configuration:");
        _logger.LogInformation(" > ConnectionString (from config): {ConnStr}", connectionString); // Log para ver o que foi pego
        _logger.LogInformation(" > DatabaseName: {DbName}", databaseName);
        _logger.LogInformation(" > LiveEventsCollectionName: {LiveCol}", liveEventsCollectionName);
        _logger.LogInformation(" > ScheduleCollectionName: {SchedCol}", scheduleCollectionName);

        if (string.IsNullOrEmpty(connectionString) || connectionString.StartsWith("DEFINIR_VIA"))
        {
            _logger.LogCritical("MongoService: ConnectionString inválida ou não resolvida: '{ConnStr}'", connectionString);
            throw new MongoConfigurationException($"ConnectionString inválida ou não resolvida: '{connectionString}'. Verifique User Secrets ou variáveis de ambiente.");
        }

        try
        {
            _mongoClient = new MongoClient(connectionString);
            var mongoDatabase = _mongoClient.GetDatabase(databaseName);
            _liveEventsCollection = mongoDatabase.GetCollection<LiveEvent>(liveEventsCollectionName);
            _scheduleCollection = mongoDatabase.GetCollection<MonitoringWindow>(scheduleCollectionName);
            _logger.LogInformation("MongoDB client initialized successfully. Database: '{DatabaseName}', LiveEvents Collection: '{LiveCol}', Schedule Collection: '{SchedCol}'.",
                                   databaseName, liveEventsCollectionName, scheduleCollectionName);
        }
        catch (MongoConfigurationException ex)
        {
            _logger.LogCritical(ex, "Failed to initialize MongoDB client due to configuration error. Connection String USADA: {ConnectionString}", connectionString);
            throw;
        }
        catch (MongoConnectionException ex)
        {
            _logger.LogCritical(ex, "Failed to connect to MongoDB server. Connection String USADA: {ConnectionString}. Please ensure the server is running and accessible.", connectionString);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "An unexpected error occurred during MongoDB client initialization. Connection String USADA: {ConnectionString}", connectionString);
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
}