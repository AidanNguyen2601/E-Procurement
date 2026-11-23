using EProcurement.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Data;

public class EProcDbContext : DbContext
{
    public EProcDbContext(DbContextOptions<EProcDbContext> options) : base(options) { }

    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Item> Items => Set<Item>();
    public DbSet<Vendor> Vendors => Set<Vendor>();
    public DbSet<VendorCategory> VendorCategories => Set<VendorCategory>();
    public DbSet<PurchaseRequest> PurchaseRequests => Set<PurchaseRequest>();
    public DbSet<PurchaseRequestLine> PurchaseRequestLines => Set<PurchaseRequestLine>();
    public DbSet<Rfq> Rfqs => Set<Rfq>();
    public DbSet<RfqLine> RfqLines => Set<RfqLine>();
    public DbSet<RfqVendor> RfqVendors => Set<RfqVendor>();
    public DbSet<VendorQuoteLine> VendorQuoteLines => Set<VendorQuoteLine>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<PurchaseOrderLine> PurchaseOrderLines => Set<PurchaseOrderLine>();
    public DbSet<GoodsReceipt> GoodsReceipts => Set<GoodsReceipt>();
    public DbSet<GoodsReceiptLine> GoodsReceiptLines => Set<GoodsReceiptLine>();
    public DbSet<VendorEvaluation> VendorEvaluations => Set<VendorEvaluation>();
    public DbSet<ApprovalHistory> ApprovalHistories => Set<ApprovalHistory>();
    public DbSet<DirectAward> DirectAwards => Set<DirectAward>();
    public DbSet<DirectAwardLine> DirectAwardLines => Set<DirectAwardLine>();
    public DbSet<Attachment> Attachments => Set<Attachment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<AppUser>().HasIndex(x => x.Username).IsUnique();
        modelBuilder.Entity<Category>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<Item>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<Vendor>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<Vendor>().HasIndex(x => x.TaxCode).IsUnique();
        modelBuilder.Entity<VendorCategory>().HasIndex(x => new { x.VendorId, x.CategoryId }).IsUnique();
        modelBuilder.Entity<PurchaseRequest>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<Rfq>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<RfqVendor>().HasIndex(x => new { x.RfqId, x.VendorId }).IsUnique();
        modelBuilder.Entity<PurchaseOrder>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<GoodsReceipt>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<ApprovalHistory>().HasIndex(x => new { x.DocumentType, x.DocumentId });
        modelBuilder.Entity<DirectAward>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<Attachment>().HasIndex(x => new { x.DocumentType, x.DocumentId });

        // Chứng từ mua sắm không được xóa dây chuyền
        foreach (var fk in modelBuilder.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()))
            fk.DeleteBehavior = DeleteBehavior.Restrict;
    }
}

/// <summary>Sinh số chứng từ dạng PR-2026-0001</summary>
public static class DocNumbers
{
    public static async Task<string> NextAsync(IQueryable<string> existing, string prefix)
    {
        var p = $"{prefix}-{DateTime.Today:yyyy}-";
        var n = await existing.CountAsync(x => x.StartsWith(p)) + 1;
        string no;
        do no = $"{p}{n++:0000}"; while (await existing.AnyAsync(x => x == no));
        return no;
    }
}
