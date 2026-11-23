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
    public DbSet<Tender> Tenders => Set<Tender>();
    public DbSet<TenderLine> TenderLines => Set<TenderLine>();
    public DbSet<TenderBidder> TenderBidders => Set<TenderBidder>();
    public DbSet<TenderBidLine> TenderBidLines => Set<TenderBidLine>();
    public DbSet<TenderCommitteeMember> TenderCommitteeMembers => Set<TenderCommitteeMember>();
    public DbSet<TenderScore> TenderScores => Set<TenderScore>();
    public DbSet<TenderClarification> TenderClarifications => Set<TenderClarification>();
    public DbSet<TenderComplaint> TenderComplaints => Set<TenderComplaint>();
    public DbSet<Contract> Contracts => Set<Contract>();
    public DbSet<ContractLine> ContractLines => Set<ContractLine>();
    public DbSet<ReportDefinition> ReportDefinitions => Set<ReportDefinition>();
    public DbSet<CatalogEntry> CatalogEntries => Set<CatalogEntry>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Budget> Budgets => Set<Budget>();
    public DbSet<ApprovalLimit> ApprovalLimits => Set<ApprovalLimit>();
    public DbSet<ApprovalDelegation> ApprovalDelegations => Set<ApprovalDelegation>();

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
        modelBuilder.Entity<Tender>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<TenderBidder>().HasIndex(x => new { x.TenderId, x.VendorId }).IsUnique();
        modelBuilder.Entity<TenderCommitteeMember>().HasIndex(x => new { x.TenderId, x.UserId }).IsUnique();
        modelBuilder.Entity<TenderScore>().HasIndex(x => new { x.TenderBidderId, x.MemberUserId }).IsUnique();
        modelBuilder.Entity<Contract>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<CatalogEntry>().HasIndex(x => new { x.Type, x.Name }).IsUnique();
        modelBuilder.Entity<Notification>().HasIndex(x => new { x.UserId, x.ReadAt });
        modelBuilder.Entity<Budget>().HasIndex(x => new { x.Year, x.Department, x.CategoryId }).IsUnique();
        modelBuilder.Entity<ApprovalDelegation>().HasIndex(x => new { x.ToUserId, x.StartDate, x.EndDate });
        modelBuilder.Entity<ApprovalDelegation>().HasOne(x => x.FromUser).WithMany().HasForeignKey(x => x.FromUserId);
        modelBuilder.Entity<ApprovalDelegation>().HasOne(x => x.ToUser).WithMany().HasForeignKey(x => x.ToUserId);
        modelBuilder.Entity<Notification>().HasIndex(x => new { x.UserId, x.Key }).IsUnique().HasFilter("[Key] IS NOT NULL");

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
