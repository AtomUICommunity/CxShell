using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CxShell.Models;
using CxShell.Services.Agent;

namespace CxShell.Services;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(SessionData))]
[JsonSerializable(typeof(SessionInfo))]
[JsonSerializable(typeof(SessionGroup))]
[JsonSerializable(typeof(ApplicationSettings))]
[JsonSerializable(typeof(List<ConnectionAuditEntry>))]
[JsonSerializable(typeof(IReadOnlyList<ConnectionAuditEntry>))]
[JsonSerializable(typeof(ConnectionAuditEntry))]
[JsonSerializable(typeof(SessionRecording))]
[JsonSerializable(typeof(SftpTransferQueueData))]
[JsonSerializable(typeof(SftpTransferQueueRecord))]
[JsonSerializable(typeof(SshHostKeyTrustService.KnownHostsFile))]
[JsonSerializable(typeof(KnownSshHostKey))]
[JsonSerializable(typeof(CommandLineHandoffService.HandoffPayload))]
[JsonSerializable(typeof(BastionTokenExchangeService.TokenExchangeRequest))]
[JsonSerializable(typeof(List<AgentAuditEntry>))]
[JsonSerializable(typeof(AgentAuditEntry[]))]
[JsonSerializable(typeof(AgentAuditEntry))]
[JsonSerializable(typeof(List<AgentRuntimeRunSnapshot>))]
[JsonSerializable(typeof(AgentRuntimeRunSnapshot[]))]
[JsonSerializable(typeof(List<AgentRunRecoveryState>))]
[JsonSerializable(typeof(AgentRunRecoveryState[]))]
internal partial class AppJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ApplicationSettings))]
[JsonSerializable(typeof(SessionData))]
[JsonSerializable(typeof(SessionInfo))]
[JsonSerializable(typeof(IReadOnlyList<ConnectionAuditEntry>))]
[JsonSerializable(typeof(List<ConnectionAuditEntry>))]
[JsonSerializable(typeof(SessionRecording))]
[JsonSerializable(typeof(List<AgentRuntimeRunSnapshot>))]
[JsonSerializable(typeof(AgentRuntimeRunSnapshot[]))]
[JsonSerializable(typeof(List<AgentRunRecoveryState>))]
[JsonSerializable(typeof(AgentRunRecoveryState[]))]
internal partial class AppIndentedJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(List<AgentConversationHistoryRecord>))]
[JsonSerializable(typeof(AgentConversationHistoryRecord))]
[JsonSerializable(typeof(AgentConversationMessageRecord))]
[JsonSerializable(typeof(AgentConversationAnnotationRecord))]
[JsonSerializable(typeof(SqliteAgentConversationHistoryStore.ConversationPayload))]
[JsonSerializable(typeof(AgentRuntimeClient.RuntimeRequestEnvelope), TypeInfoPropertyName = "AgentRuntimeClientRequestEnvelope")]
[JsonSerializable(typeof(AgentRuntimeStreamTransport.RuntimeRequestEnvelope), TypeInfoPropertyName = "AgentRuntimeStreamRequestEnvelope")]
[JsonSerializable(typeof(AgentRuntimeRequest))]
[JsonSerializable(typeof(AgentRuntimeResponse))]
[JsonSerializable(typeof(AgentRuntimeEventEnvelope))]
[JsonSerializable(typeof(AgentRuntimeStreamEnvelope))]
[JsonSerializable(typeof(AgentRuntimeStreamEvent))]
[JsonSerializable(typeof(AgentRuntimeInitializeResult))]
[JsonSerializable(typeof(AgentRuntimePingResult))]
[JsonSerializable(typeof(AgentRuntimeRequestCancelResult))]
[JsonSerializable(typeof(AgentRuntimeInfoResult))]
[JsonSerializable(typeof(AgentRuntimeCapabilityResult))]
[JsonSerializable(typeof(AgentRuntimeProviderStatusResult))]
[JsonSerializable(typeof(AgentRuntimeProviderTestResult))]
[JsonSerializable(typeof(AgentRuntimeProviderModelsResult))]
[JsonSerializable(typeof(AgentRuntimeWebSearchResult))]
[JsonSerializable(typeof(AgentRuntimeWebFetchResult))]
[JsonSerializable(typeof(AgentRuntimeModelRequestResult))]
[JsonSerializable(typeof(AgentRuntimeToolDescriptor))]
[JsonSerializable(typeof(AgentRuntimeToolCatalogResult))]
[JsonSerializable(typeof(AgentRuntimeSessionListResult))]
[JsonSerializable(typeof(AgentRuntimeSavedSessionListResult))]
[JsonSerializable(typeof(AgentRuntimeSavedSessionOpenResult))]
[JsonSerializable(typeof(AgentRuntimeSavedSessionCloseResult))]
[JsonSerializable(typeof(AgentRuntimeSessionGetResult))]
[JsonSerializable(typeof(AgentRuntimeAuditListResult))]
[JsonSerializable(typeof(AgentRuntimeRunListResult))]
[JsonSerializable(typeof(AgentRuntimeRunStatusResult))]
[JsonSerializable(typeof(AgentRuntimeRunClearResult))]
[JsonSerializable(typeof(AgentRuntimeRunEventsResult))]
[JsonSerializable(typeof(AgentRuntimeSessionCommandResult))]
[JsonSerializable(typeof(AgentRuntimeCancelResult))]
[JsonSerializable(typeof(AgentRuntimeRunAppendResult))]
[JsonSerializable(typeof(AgentRuntimeRunStopResult))]
[JsonSerializable(typeof(AgentRuntimeRunResumeResult))]
[JsonSerializable(typeof(AgentRuntimeRunApprovalResult))]
[JsonSerializable(typeof(AgentRuntimeRunCredentialResult))]
[JsonSerializable(typeof(AgentRuntimeSessionCommandCancelResult))]
[JsonSerializable(typeof(AgentRuntimeSessionCommandApprovalResult))]
[JsonSerializable(typeof(AgentRuntimeRunResult))]
[JsonSerializable(typeof(AgentRuntimeRunSnapshot))]
[JsonSerializable(typeof(AgentRunRecoveryState))]
[JsonSerializable(typeof(AgentRunCheckpoint))]
[JsonSerializable(typeof(AgentRunStep))]
[JsonSerializable(typeof(AgentContextEstimate))]
[JsonSerializable(typeof(AgentToolResultEnvelope))]
[JsonSerializable(typeof(AgentCommandVerification))]
[JsonSerializable(typeof(AgentCommandResult))]
[JsonSerializable(typeof(AgentAuditEntry))]
[JsonSerializable(typeof(AgentChatMessage))]
[JsonSerializable(typeof(AgentContentPart))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(object[]))]
internal partial class AppCamelCaseJsonContext : JsonSerializerContext;

internal static class AppJsonOptions
{
    public static JsonSerializerOptions Default { get; } = new()
    {
        TypeInfoResolver = JsonTypeInfoResolver.Combine(
            AppJsonContext.Default,
            new DefaultJsonTypeInfoResolver())
    };

    public static JsonSerializerOptions Indented { get; } = new()
    {
        WriteIndented = true,
        TypeInfoResolver = JsonTypeInfoResolver.Combine(
            AppIndentedJsonContext.Default,
            new DefaultJsonTypeInfoResolver())
    };

    public static JsonSerializerOptions CamelCase { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = JsonTypeInfoResolver.Combine(
            AppCamelCaseJsonContext.Default,
            new DefaultJsonTypeInfoResolver())
    };
}
