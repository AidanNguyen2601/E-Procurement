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
    public DbSet<TenderQuestion> TenderQuestions => Set<TenderQuestion>();
    public DbSet<TenderNegotiation> TenderNegotiations => Set<TenderNegotiation>();
    public DbSet<TenderAppraisal> TenderAppraisals => Set<TenderAppraisal>();
    public DbSet<ProcurementPackage> ProcurementPackages => Set<ProcurementPackage>();
    public DbSet<SelectionPlan> SelectionPlans => Set<SelectionPlan>();
    public DbSet<Prequalification> Prequalifications => Set<Prequalification>();
    public DbSet<PrequalificationCriterion> PrequalificationCriteria => Set<PrequalificationCriterion>();
    public DbSet<PrequalificationApplication> PrequalificationApplications => Set<PrequalificationApplication>();
    public DbSet<PrequalificationScore> PrequalificationScores => Set<PrequalificationScore>();
    public DbSet<PrequalificationDocument> PrequalificationDocuments => Set<PrequalificationDocument>();
    public DbSet<PriceRecord> PriceRecords => Set<PriceRecord>();
    public DbSet<StandardPriceList> StandardPriceLists => Set<StandardPriceList>();
    public DbSet<StandardPriceLine> StandardPriceLines => Set<StandardPriceLine>();
    public DbSet<Contract> Contracts => Set<Contract>();
    public DbSet<ContractLine> ContractLines => Set<ContractLine>();
    public DbSet<ReportDefinition> ReportDefinitions => Set<ReportDefinition>();
    public DbSet<CatalogEntry> CatalogEntries => Set<CatalogEntry>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Budget> Budgets => Set<Budget>();
    public DbSet<ApprovalLimit> ApprovalLimits => Set<ApprovalLimit>();
    public DbSet<ApprovalDelegation> ApprovalDelegations => Set<ApprovalDelegation>();
    public DbSet<VendorUser> VendorUsers => Set<VendorUser>();
    public DbSet<VendorDocument> VendorDocuments => Set<VendorDocument>();
    public DbSet<VendorSubmission> VendorSubmissions => Set<VendorSubmission>();
    public DbSet<PoShipment> PoShipments => Set<PoShipment>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<ContractAmendment> ContractAmendments => Set<ContractAmendment>();
    public DbSet<ContractMilestone> ContractMilestones => Set<ContractMilestone>();
    public DbSet<ContractGuarantee> ContractGuarantees => Set<ContractGuarantee>();
    public DbSet<ApiClient> ApiClients => Set<ApiClient>();
    public DbSet<ProcurementPlan> ProcurementPlans => Set<ProcurementPlan>();
    public DbSet<ProcurementPlanLine> ProcurementPlanLines => Set<ProcurementPlanLine>();
    public DbSet<DocumentSignature> DocumentSignatures => Set<DocumentSignature>();

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
        modelBuilder.Entity<TenderQuestion>().HasIndex(x => new { x.TenderId, x.ClarificationId });
        modelBuilder.Entity<ProcurementPackage>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<ProcurementPackage>().HasMany(x => x.PurchaseRequests).WithOne(p => p.Package).HasForeignKey(p => p.PackageId);
        modelBuilder.Entity<SelectionPlan>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<SelectionPlan>().HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedById);
        modelBuilder.Entity<SelectionPlan>().HasOne(x => x.DecidedBy).WithMany().HasForeignKey(x => x.DecidedById);
        modelBuilder.Entity<TenderAppraisal>().HasOne(x => x.Appraiser).WithMany().HasForeignKey(x => x.AppraiserId);
        modelBuilder.Entity<TenderAppraisal>().HasOne(x => x.AssignedBy).WithMany().HasForeignKey(x => x.AssignedById);
        modelBuilder.Entity<VendorSubmission>().HasIndex(x => new { x.DirectAwardId, x.IsLatest });
        modelBuilder.Entity<Prequalification>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<Prequalification>().HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedById);
        modelBuilder.Entity<Prequalification>().HasOne(x => x.DecidedBy).WithMany().HasForeignKey(x => x.DecidedById);
        modelBuilder.Entity<PrequalificationApplication>().HasIndex(x => new { x.PrequalificationId, x.VendorId }).IsUnique();
        modelBuilder.Entity<PrequalificationScore>().HasIndex(x => new { x.ApplicationId, x.CriterionId }).IsUnique();
        modelBuilder.Entity<PriceRecord>().HasIndex(x => new { x.ItemId, x.PriceDate });
        modelBuilder.Entity<StandardPriceList>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<StandardPriceList>().HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedById);
        modelBuilder.Entity<StandardPriceList>().HasOne(x => x.DecidedBy).WithMany().HasForeignKey(x => x.DecidedById);
        modelBuilder.Entity<StandardPriceLine>().HasIndex(x => new { x.ListId, x.ItemId }).IsUnique();
        modelBuilder.Entity<Contract>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<CatalogEntry>().HasIndex(x => new { x.Type, x.Name }).IsUnique();
        modelBuilder.Entity<Notification>().HasIndex(x => new { x.UserId, x.ReadAt });
        modelBuilder.Entity<Budget>().HasIndex(x => new { x.Year, x.Department, x.CategoryId }).IsUnique();
        modelBuilder.Entity<ApprovalDelegation>().HasIndex(x => new { x.ToUserId, x.StartDate, x.EndDate });
        modelBuilder.Entity<VendorUser>().HasIndex(x => x.Email).IsUnique();
        modelBuilder.Entity<VendorDocument>().HasIndex(x => new { x.VendorId, x.ExpiryDate });
        modelBuilder.Entity<VendorSubmission>().HasIndex(x => new { x.RfqVendorId, x.IsLatest });
        modelBuilder.Entity<VendorSubmission>().HasIndex(x => new { x.TenderBidderId, x.IsLatest });
        modelBuilder.Entity<PoShipment>().HasIndex(x => x.PurchaseOrderId);
        modelBuilder.Entity<Invoice>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<ApiClient>().HasIndex(x => x.KeyHash).IsUnique();
        modelBuilder.Entity<DocumentSignature>().HasIndex(x => new { x.DocumentType, x.DocumentId });
        modelBuilder.Entity<ProcurementPlan>().HasIndex(x => new { x.Year, x.Department }).IsUnique();
        modelBuilder.Entity<ProcurementPlan>().HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedById);
        modelBuilder.Entity<ProcurementPlan>().HasOne(x => x.ApprovedBy).WithMany().HasForeignKey(x => x.ApprovedById);
        modelBuilder.Entity<Invoice>().HasIndex(x => new { x.VendorId, x.InvoiceNo, x.Series });
        modelBuilder.Entity<Payment>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<Payment>().HasIndex(x => new { x.Status, x.RequestedAt });
        modelBuilder.Entity<Payment>().HasOne(x => x.RequestedBy).WithMany().HasForeignKey(x => x.RequestedById);
        modelBuilder.Entity<Payment>().HasOne(x => x.ApprovedBy).WithMany().HasForeignKey(x => x.ApprovedById);
        modelBuilder.Entity<Payment>().HasOne(x => x.PaidBy).WithMany().HasForeignKey(x => x.PaidById);
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
