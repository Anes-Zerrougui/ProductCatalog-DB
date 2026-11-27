using System.ComponentModel.DataAnnotations;

namespace ProductCatalog.Web.Models.ViewModels;

/// <summary>One row in the group list.</summary>
public class ProductGroupListItem
{
    public int GroupId { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int ProductCount { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>The list page: the search text plus the rows.</summary>
public class ProductGroupIndexViewModel
{
    public string? Search { get; set; }
    public List<ProductGroupListItem> Items { get; set; } = new();
}

/// <summary>Used by both the Create and the Edit form.</summary>
public class ProductGroupFormViewModel
{
    public int GroupId { get; set; }

    [Required(ErrorMessage = "Group name is required.")]
    [StringLength(100, ErrorMessage = "Group name can be at most 100 characters.")]
    [Display(Name = "Group name")]
    public string GroupName { get; set; } = string.Empty;

    [StringLength(500, ErrorMessage = "Description can be at most 500 characters.")]
    [Display(Name = "Description")]
    public string? Description { get; set; }
}

/// <summary>Delete confirmation page.</summary>
public class ProductGroupDeleteViewModel
{
    public int GroupId { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Products that are not soft-deleted.</summary>
    public int ActiveProductCount { get; set; }

    /// <summary>All products, including soft-deleted ones (the database foreign key blocks all of them).</summary>
    public int TotalProductCount { get; set; }
}
