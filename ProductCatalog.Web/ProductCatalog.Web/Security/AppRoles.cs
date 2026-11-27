namespace ProductCatalog.Web.Security;

/// <summary>
/// Role names. They must match the values in the role table (role_name).
/// </summary>
public static class AppRoles
{
    public const string Admin = "Admin";
    public const string Editor = "Editor";
    public const string Viewer = "Viewer";

    // Handy combinations for [Authorize(Roles = ...)]
    public const string AdminOrEditor = Admin + "," + Editor;
}
