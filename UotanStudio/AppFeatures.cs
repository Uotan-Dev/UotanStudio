namespace UotanStudio;

/// <summary>Availability of retained application features.<para>保留功能的可用性。</para></summary>
public static class AppFeatures
{
    /// <summary>Keeps the AI assistant feature unavailable in every build.<para>AI 助手功能在所有构建中保持不可用。</para></summary>
    /// <value>Always false.<para>始终为 false。</para></value>
    public static bool AiAssistantEnabled => false;
}