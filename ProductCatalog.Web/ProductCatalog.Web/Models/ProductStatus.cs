namespace ProductCatalog.Web.Models;

/// <summary>Allowed values of product.status (the database CHECK constraint uses the same three values).</summary>
public static class ProductStatus
{
    public const string Draft = "Draft";
    public const string Active = "Active";
    public const string Inactive = "Inactive";

    public static readonly string[] All = { Draft, Active, Inactive };
}
