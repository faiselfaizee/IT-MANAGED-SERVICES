namespace DeskAsk.Platform.Assets;

/// <summary>
/// Who may look at Assets. Customer employees (role "customer") have no access; a customer administrator ("cadmin") reads only.
/// Changing anything still needs the administrator role until delegation (the access module) takes over.
/// </summary>
public static class AssetsAccess
{
    public static readonly string[] Readers = { "admin", "engineer", "viewer", "cadmin" };
}
