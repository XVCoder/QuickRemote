namespace QuickRemote.PCClient.Models;

/// <summary>
/// 与中转服务器的连接状态。
/// </summary>
public enum ConnectionStatus
{
    /// <summary>已断开</summary>
    Disconnected,
    /// <summary>连接中</summary>
    Connecting,
    /// <summary>已连接</summary>
    Connected,
    /// <summary>重连中</summary>
    Reconnecting
}
