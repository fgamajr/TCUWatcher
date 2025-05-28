using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace TCUWatcher.API.Models;

/// <summary>
/// Representa as configurações globais da aplicação armazenadas no MongoDB.
/// </summary>
public class AppConfiguration
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; } // ID do documento no MongoDB

    [BsonElement("configName")]
    public string ConfigName { get; set; } = "global_app_settings"; // Nome fixo para identificar o documento de configuração global

    [BsonElement("syncIntervalMinutes")]
    public int SyncIntervalMinutes { get; set; } = 30; // Valor padrão, será sobrescrito pelo DB

    [BsonElement("initialDelaySeconds")]
    public int InitialDelaySeconds { get; set; } = 15; // Valor padrão, será sobrescrito pelo DB

    [BsonElement("maxMissCountBeforeOffline")]
    public int MaxMissCountBeforeOffline { get; set; } = 2; // Valor padrão, será sobrescrito pelo DB
    
    // Adicione outras configurações globais aqui se precisar de mais flexibilidade
    // Ex: "youtubeApiQueryLimitPerMinute", "webhookRetryCount"
}