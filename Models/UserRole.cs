namespace OpenTrafficDB.Models;

public enum UserRole
{
    /// <summary>Registered user, has not yet contributed enough data.</summary>
    User,

    /// <summary>Regularly contributes valid GPS observations (auto-promoted).</summary>
    Contributor,

    /// <summary>Administrator with full management access.</summary>
    Admin
}
