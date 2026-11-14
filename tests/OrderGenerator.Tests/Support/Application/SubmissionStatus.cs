namespace OrderGenerator;

public enum SubmissionStatus
{
    Confirmed,
    PendingConfirmation,
    ValidationFailed,
    IdentityConflict,
    TransportUnavailable,
    CapacityReached
}
