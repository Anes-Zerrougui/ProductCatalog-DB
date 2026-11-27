using System;
using System.Collections.Generic;

namespace ProductCatalog.Web.Models;

public partial class VProductList
{
    public int? ProductId { get; set; }

    public string? ProductName { get; set; }

    public string? Sku { get; set; }

    public decimal? Price { get; set; }

    public int? StockQuantity { get; set; }

    public string? Status { get; set; }

    public int? GroupId { get; set; }

    public string? GroupName { get; set; }

    public string? PrimaryImageUrl { get; set; }

    public string? Tags { get; set; }
}
