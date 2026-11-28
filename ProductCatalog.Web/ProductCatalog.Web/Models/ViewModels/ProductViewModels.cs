using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace ProductCatalog.Web.Models.ViewModels;

/// <summary>One row in the product list.</summary>
public class ProductListItem
{
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int StockQuantity { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
    public string? PrimaryImageUrl { get; set; }
    public List<string> Tags { get; set; } = new();
}

/// <summary>The list page: filters, sorting, paging and the rows.</summary>
public class ProductIndexViewModel
{
    public string? Search { get; set; }
    public int? GroupId { get; set; }
    public string? Status { get; set; }
    public string? Tag { get; set; }
    public string Sort { get; set; } = "name";
    public bool Desc { get; set; }

    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    public int TotalCount { get; set; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));

    public List<ProductListItem> Items { get; set; } = new();
    public List<SelectListItem> Groups { get; set; } = new();
    public List<SelectListItem> Statuses { get; set; } = new();
    public List<SelectListItem> Tags { get; set; } = new();
}

/// <summary>One image row in the product form.</summary>
public class ProductImageInput
{
    [StringLength(500, ErrorMessage = "The address can be at most 500 characters.")]
    public string? ImageUrl { get; set; }
}

/// <summary>One attribute row (name + value) in the product form.</summary>
public class ProductAttributeInput
{
    [StringLength(100, ErrorMessage = "Name can be at most 100 characters.")]
    public string? Name { get; set; }

    [StringLength(255, ErrorMessage = "Value can be at most 255 characters.")]
    public string? Value { get; set; }
}

/// <summary>Used by both the Create and the Edit form.</summary>
public class ProductFormViewModel
{
    public int ProductId { get; set; }
    public bool IsEdit { get; set; }

    [Required(ErrorMessage = "Product name is required.")]
    [StringLength(200, ErrorMessage = "Product name can be at most 200 characters.")]
    [Display(Name = "Product name")]
    public string ProductName { get; set; } = string.Empty;

    [Required(ErrorMessage = "SKU is required.")]
    [StringLength(50, ErrorMessage = "SKU can be at most 50 characters.")]
    [Display(Name = "SKU")]
    public string Sku { get; set; } = string.Empty;

    [Display(Name = "Description")]
    public string? Description { get; set; }

    [Required(ErrorMessage = "Price is required.")]
    [Range(typeof(decimal), "0.01", "9999999999.99", ErrorMessage = "Price must be greater than 0.")]
    [Display(Name = "Price")]
    public decimal? Price { get; set; }

    // Only used when creating. After that, stock is changed with the "Adjust stock" procedure.
    [Range(0, int.MaxValue, ErrorMessage = "Stock cannot be negative.")]
    [Display(Name = "Initial stock")]
    public int StockQuantity { get; set; }

    // Only shown when editing
    public int CurrentStock { get; set; }

    [Required(ErrorMessage = "Please choose a status.")]
    [Display(Name = "Status")]
    public string Status { get; set; } = ProductStatus.Draft;

    [Required(ErrorMessage = "Please choose a group.")]
    [Display(Name = "Product group")]
    public int? GroupId { get; set; }

    // --- one-to-many parts of the product ---
    public List<ProductImageInput> Images { get; set; } = new();
    public int PrimaryImageIndex { get; set; }
    public List<ProductAttributeInput> Attributes { get; set; } = new();

    // --- many-to-many part: tags typed as "business, portable" ---
    [Display(Name = "Tags")]
    public string? TagsText { get; set; }

    // dropdown data and hints (not posted)
    public List<SelectListItem> Groups { get; set; } = new();
    public List<SelectListItem> Statuses { get; set; } = new();
    public List<string> ExistingTags { get; set; } = new();
}

public class ProductHistoryItem
{
    public DateTime ChangedAt { get; set; }
    public string? ChangedBy { get; set; }
    public decimal? OldPrice { get; set; }
    public decimal? NewPrice { get; set; }
    public int? OldStock { get; set; }
    public int? NewStock { get; set; }
}

public class ProductImageItem
{
    public string Url { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }
}

public class ProductAttributeItem
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public class ProductDetailsViewModel
{
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal Price { get; set; }
    public int StockQuantity { get; set; }
    public string Status { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<ProductImageItem> Images { get; set; } = new();
    public List<ProductAttributeItem> Attributes { get; set; } = new();
    public List<string> Tags { get; set; } = new();

    /// <summary>Last price/stock changes, written automatically by the audit trigger.</summary>
    public List<ProductHistoryItem> History { get; set; } = new();
}

public class ProductDeleteViewModel
{
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
}

public class AdjustStockViewModel
{
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public int CurrentStock { get; set; }

    [Required(ErrorMessage = "Please enter an amount.")]
    [Range(-1000000, 1000000, ErrorMessage = "Amount must be between -1,000,000 and 1,000,000.")]
    [Display(Name = "Change amount")]
    public int? Amount { get; set; }
}
