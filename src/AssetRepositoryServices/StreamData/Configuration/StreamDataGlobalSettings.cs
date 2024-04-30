namespace Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Configuration;

/// <summary>
/// This represents the status whether the stream data is enabled or not.
/// This must not be in the StreamDataCk because the Ck may not be imported yet.
/// </summary>
internal class StreamDataGlobalSettings
{
    public static StreamDataGlobalSettings Enabled => new() { IsEnabled = true };
    public static StreamDataGlobalSettings Disabled => new() { IsEnabled = false };

    
    
    /// <summary>
    /// stream data enabled for a given tenant.
    /// </summary>
    public bool IsEnabled { get; set; }
}