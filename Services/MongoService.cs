using TCUWatcher.API.Models;
using MongoDB.Driver;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging; // Para ILogger
using System; // Para Exception
using System.Collections.Generic; // Para List
using System.Threading.Tasks; // Para Task

namespace TCUWatcher.API.Services;

public class MongoService : IMongoService
{
    private readonly IMongoCollection<LiveEvent> _liveEventsCollection;
    private readonly ILogger<MongoService> _logger;
    private readonly MongoClient _mongoClient;

    public class MongoDbSettings
    {
        public string ConnectionString { get; set; } = null!;
        public string DatabaseName { get; set; } = null!;
        public string CollectionName { get; set; } = null!;
    }

    public MongoService(IOptions<MongoDbSettings> mongoDbSettings, ILogger<MongoService> logger)
    {
        _logger = logger;
        try
        {
            _mongoClient = new MongoClient(mongoDbSettings.Value.ConnectionString);
            var mongoDatabase = _mongoClient.GetDatabase(mongoDbSettings.Value.DatabaseName);
            _liveEventsCollection = mongoDatabase.GetCollection<LiveEvent>(mongoDbSettings.Value.CollectionName);
            _logger.LogInformation("MongoDB client initialized successfully for database '{DatabaseName}'.", mongoDbSettings.Value.DatabaseName);
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
            _logger.LogError(ex, "Error fetching live events from MongoDB with filter and sort.");
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
}
