namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>Describes observed power-command evidence, not successful boot into an operating system.</summary>
public enum FirehosePowerStatus
{
    /// <summary>No power command has been attempted on this protocol instance.</summary>
    NotRequested,
    /// <summary>The power command is being sent or its response is pending.</summary>
    AwaitingResponse,
    /// <summary>The target returned a normal-mode NAK.</summary>
    Rejected,
    /// <summary>The target acknowledged the command; its subsequent boot state is unverified.</summary>
    Acknowledged,
    /// <summary>The target acknowledged the command and the transport later reported disconnected.</summary>
    AcknowledgedAndDisconnected,
    /// <summary>The command outcome is uncertain because no valid ACK or NAK was received.</summary>
    Unknown
}