using System.Linq;

namespace POS.Identity.Authorization;

/// <summary>
/// The permission catalogue as constants.
/// </summary>
/// <remarks>
/// String literals at call sites produce typos that fail OPEN — a misspelled
/// permission check may match nothing and, depending on the handler, silently allow
/// or silently deny. Constants make the compiler the first line of defence, and
/// make "who can do X" answerable by find-usages.
///
/// Convention: module.resource.action[.qualifier]
/// </remarks>
public static class Permissions
{
    /// <summary>Every permission code in the catalogue, gathered by reflection.</summary>
    /// <remarks>
    /// Exists so a one-time "grant everything" operation (the Owner role a newly
    /// provisioned tenant gets — see <c>ProvisioningEndpoints</c>) never has to be
    /// kept in step by hand with this file as permissions are added. Reflection over
    /// public const string fields one level down; a differently-shaped addition
    /// would silently miss this, which is an acceptable trade for a bootstrap
    /// convenience rather than an enforcement mechanism.
    /// </remarks>
    public static IReadOnlyList<string> AllCodes { get; } = typeof(Permissions)
        .GetNestedTypes(System.Reflection.BindingFlags.Public)
        .SelectMany(t => t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToList();

    public static class Sales
    {
        public const string Create = "sales.transaction.create";
        public const string Void = "sales.transaction.void";
        public const string Suspend = "sales.transaction.suspend";
        public const string DiscountLine = "sales.discount.line";
        public const string DiscountOrder = "sales.discount.order";

        /// <summary>Beyond the cashier's configured limit. The classic manager override.</summary>
        public const string DiscountOverride = "sales.discount.override";

        public const string RefundCreate = "sales.refund.create";
        public const string RefundApprove = "sales.refund.approve";
        public const string RefundWithoutReceipt = "sales.refund.blind";
        public const string PriceOverride = "sales.price.override";
        public const string ReprintReceipt = "sales.receipt.reprint";
    }

    public static class Cash
    {
        public const string OpenShift = "cash.shift.open";
        public const string CloseShift = "cash.shift.close";
        public const string OpenDrawerNoSale = "cash.drawer.nosale";
        public const string Drop = "cash.drop.create";
        public const string Pickup = "cash.pickup.create";
        public const string ViewVariance = "cash.variance.view";
    }

    public static class Catalog
    {
        public const string ProductView = "catalog.product.view";
        public const string ProductCreate = "catalog.product.create";
        public const string ProductEdit = "catalog.product.edit";
        public const string ProductDelete = "catalog.product.delete";
        public const string PriceEdit = "catalog.price.edit";
        public const string PriceApprove = "catalog.price.approve";
        public const string BarcodeManage = "catalog.barcode.manage";
    }

    public static class Inventory
    {
        public const string View = "inventory.stock.view";
        public const string AdjustmentCreate = "inventory.adjustment.create";
        public const string AdjustmentApprove = "inventory.adjustment.approve";
        public const string TransferCreate = "inventory.transfer.create";
        public const string TransferReceive = "inventory.transfer.receive";
        public const string TransferWriteOffVarianceSupervisor = "inventory.transfer.writeoff.supervisor";
        public const string TransferWriteOffVarianceManager = "inventory.transfer.writeoff.manager";
        public const string TransferWriteOffVarianceDirector = "inventory.transfer.writeoff.director";
        public const string CountPerform = "inventory.count.perform";
        public const string CountApprove = "inventory.count.approve";
    }

    public static class Purchasing
    {
        public const string SupplierView = "purchasing.supplier.view";
        public const string SupplierManage = "purchasing.supplier.manage";

        public const string OrderView = "purchasing.order.view";
        public const string OrderRaise = "purchasing.order.raise";
        public const string OrderCancel = "purchasing.order.cancel";

        /// <summary>
        /// Approval is split by LEVEL because the threshold is the control.
        /// </summary>
        /// <remarks>
        /// A single "can approve" permission cannot express "a supervisor may sign off
        /// £900 but not £90,000", which is the entire point of an approval ladder
        /// (ADR 049). The endpoint resolves the HIGHEST level the user holds and offers
        /// that to the domain, which then decides whether it is sufficient for the
        /// order's value. Separation of duties — the raiser may not approve their own
        /// order — is enforced by the aggregate, not here.
        /// </remarks>
        public const string OrderApproveSupervisor = "purchasing.order.approve.supervisor";

        public const string OrderApproveManager = "purchasing.order.approve.manager";
        public const string OrderApproveDirector = "purchasing.order.approve.director";

        public const string ReceiptView = "purchasing.receipt.view";
        public const string ReceiptCreate = "purchasing.receipt.create";
        public const string ReceiptPost = "purchasing.receipt.post";

        public const string InvoiceView = "purchasing.invoice.view";
        public const string InvoiceRecord = "purchasing.invoice.record";
        public const string InvoiceApprove = "purchasing.invoice.approve";

        /// <summary>Releasing an invoice the three-way match blocked. A fraud-sensitive override.</summary>
        public const string InvoiceOverrideBlock = "purchasing.invoice.override";

        public const string ReturnView = "purchasing.return.view";
        public const string ReturnCreate = "purchasing.return.create";
        public const string ReturnDispatch = "purchasing.return.dispatch";
        public const string ReturnRecordCredit = "purchasing.return.credit";
    }

    public static class Expenses
    {
        public const string View = "expenses.expense.view";
        public const string Record = "expenses.expense.record";
        public const string Approve = "expenses.expense.approve";
        public const string Reject = "expenses.expense.reject";
    }

    public static class Administration
    {
        public const string UserManage = "admin.user.manage";
        public const string RoleManage = "admin.role.manage";
        public const string TerminalEnrol = "admin.terminal.enrol";
        public const string TerminalRevoke = "admin.terminal.revoke";
        public const string SettingsEdit = "admin.settings.edit";
        public const string AuditView = "admin.audit.view";
    }

    public static class Reports
    {
        public const string SalesView = "reports.sales.view";
        public const string FinancialView = "reports.financial.view";
        public const string MarginView = "reports.margin.view";
        public const string CashierPerformance = "reports.cashier.view";

        /// <summary>
        /// The reconciliation reports. A control function, not a business one.
        /// </summary>
        /// <remarks>
        /// Separate from the other report permissions because the audience is
        /// different: these answer "does the system agree with itself", which is a
        /// question for finance and audit rather than for a store manager looking at
        /// their takings.
        /// </remarks>
        public const string ReconciliationView = "reports.reconciliation.view";
    }
}
