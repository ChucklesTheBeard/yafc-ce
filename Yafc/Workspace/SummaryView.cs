using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Yafc.I18n;
using Yafc.Model;
using Yafc.UI;

namespace Yafc;

public class SummaryView : ProjectPageView<Summary> {
    /// <summary>Height of each row</summary>
    private float rowHeight;

    /// <summary>Some padding to have the contents of the first column not 'stick' to the rest of the UI</summary>
    private readonly Padding FirstColumnPadding = new Padding(1f, 1.5f, 0, 0);
    private static float firstColumnWidth;
    private const float DefaultScrollHeight = 10f;

    private class SummaryTabColumn : TextDataColumn<ProjectPage> {
        public SummaryTabColumn() : base(LSs.Page, firstColumnWidth) {
        }

        public override void BuildElement(ImGui gui, ProjectPage page) {
            width = firstColumnWidth;

            if (page?.contentType != typeof(ProductionTable)) {
                return;
            }

            using (gui.EnterGroup(new Padding(0.5f, 0.2f, 0.2f, 0.5f))) {
                gui.spacing = 0.2f;

                if (page.icon != null) {
                    gui.BuildIcon(page.icon.GetIcon(), FirstColumnIconSize);
                }
                else {
                    _ = gui.AllocateRect(0f, FirstColumnIconSize);
                }

                gui.BuildText(page.name);
            }
        }
    }

    private sealed class SummaryDataColumn(SummaryView view) : TextDataColumn<ProjectPage>(LSs.SummaryColumnLinked, float.MaxValue) {
        public override void BuildElement(ImGui gui, ProjectPage page) {
            if (page?.contentType != typeof(ProductionTable)) {
                return;
            }

            ProductionTable table = (ProductionTable)page.content;
            using var grid = gui.EnterInlineGrid(ElementWidth, ElementSpacing);
            foreach ((string name, GoodDetails details, bool enoughProduced) in view.GoodsToDisplay()) {
                ProductionLink? link = table.links.Find(x => x.goods.QualityName() == name);
                grid.Next();
                bool isDrawn = false;

                if (link != null) {
                    if (link.amount != 0f) {
                        DrawProvideProduct(gui, link, page, details, enoughProduced);
                        isDrawn = true;
                    }
                }
                else {
                    if (Array.Exists(table.flow, x => x.goods.QualityName() == name)) {
                        ProductionTableFlow flow = Array.Find(table.flow, x => x.goods.QualityName() == name);

                        if (Math.Abs(flow.amount) > Epsilon) {

                            DrawRequestProduct(gui, flow, enoughProduced);
                            isDrawn = true;
                        }
                    }
                }
                if (!isDrawn) {
                    // Reserve empty space to prevent 'compressing' empty rows
                    _ = gui.AllocateRect(0f, view.rowHeight);
                }
            }
        }

        private static void DrawProvideProduct(ImGui gui, ProductionLink element, ProjectPage page, GoodDetails goodInfo, bool enoughProduced) {
            SchemeColor iconColor;

            if (element.amount > 0 && enoughProduced) {
                // Production matches consumption
                iconColor = SchemeColor.Primary;
            }
            else if (element.amount < 0 && goodInfo.extraProduced == -element.amount) {
                // Extra produced matches input goal
                iconColor = SchemeColor.Primary;
            }
            else {
                // Production and consumption does not match
                iconColor = SchemeColor.Error;
            }

            gui.allocator = RectAllocator.Stretch;
            gui.spacing = 0f;
            DisplayAmount amount = new(element.amount, element.goods.target.flowUnitOfMeasure);
            GoodsWithAmountEvent evt = gui.BuildFactorioObjectWithEditableAmount(element.goods, amount, ButtonDisplayStyle.ProductionTableScaled(iconColor));

            if (evt == GoodsWithAmountEvent.TextEditing && amount.Value != 0) {
                _ = SetProviderAmount(element, page, amount.Value);
            }
            else if (evt == GoodsWithAmountEvent.LeftButtonClick) {
                _ = SetProviderAmount(element, page, YafcRounding(goodInfo.sum));
            }
        }
        private static void DrawRequestProduct(ImGui gui, ProductionTableFlow flow, bool enoughExtraProduced) {
            SchemeColor iconColor;
            if (flow.amount > Epsilon) {
                if (!enoughExtraProduced) {
                    // Not enough in extra production, while being depended on
                    iconColor = SchemeColor.Error;
                }
                else {
                    // Enough extra product produced
                    iconColor = SchemeColor.Green;
                }
            }
            else {
                // Flow amount negative or (almost, due to rounding errors) zero, just a regular request (nothing to indicate)
                iconColor = SchemeColor.None;
            }

            gui.allocator = RectAllocator.Stretch;
            gui.spacing = 0f;
            _ = gui.BuildFactorioObjectWithAmount(flow.goods, new(-flow.amount, flow.goods.target.flowUnitOfMeasure), ButtonDisplayStyle.ProductionTableScaled(iconColor));
        }

        internal static Task SetProviderAmount(ProductionLink element, ProjectPage page, float newAmount) {
            element.RecordUndo().amount = newAmount;
            return page.RunSolveJob();
        }
    }

    private static readonly float Epsilon = 1e-5f;
    private static readonly float ElementWidth = 3;
    private static readonly float ElementSpacing = 1;
    private static readonly float FirstColumnIconSize = 1.5f;

    private struct GoodDetails {
        public float totalProvided;
        public float totalNeeded;
        public float extraProduced;
        public float sum;
    }

    private Project project = null!;
    private SearchQuery searchQuery;

    private readonly LinkedScrollArea leftScrollArea;
    private readonly LinkedScrollArea rightScrollArea;
    private readonly SummaryTabColumn tabColumn;
    private readonly SummaryDataColumn goodsColumn;
    private readonly DataGrid<ProjectPage> leftGrid;
    private readonly DataGrid<ProjectPage> rightGrid;
    private readonly HashSet<ProjectPage> subscribedPages = [];

    private Dictionary<string, GoodDetails> allGoods = [];
    private float cubeOccupancy;

    private IEnumerable<(string name, GoodDetails details, bool enoughProduced)> GoodsToDisplay() {
        foreach (KeyValuePair<string, GoodDetails> goodInfo in allGoods) {
            if (!searchQuery.Match(goodInfo.Key)) {
                continue;
            }

            float amountAvailable = YafcRounding((goodInfo.Value.totalProvided > 0 ? goodInfo.Value.totalProvided : 0) + goodInfo.Value.extraProduced);
            float amountNeeded = YafcRounding((goodInfo.Value.totalProvided < 0 ? -goodInfo.Value.totalProvided : 0) + goodInfo.Value.totalNeeded);

            if (model == null || (model.showOnlyIssues && (Math.Abs(amountAvailable - amountNeeded) < Epsilon || amountNeeded == 0))) {
                continue;
            }

            bool enoughProduced = amountAvailable >= amountNeeded;

            yield return (goodInfo.Key, goodInfo.Value, enoughProduced);
        }
    }

    private IEnumerable<(string name, GoodDetails details, float excess)> GoodsToBalance() {
        foreach (KeyValuePair<string, GoodDetails> goodInfo in allGoods) {
            float amountAvailable = YafcRounding((goodInfo.Value.totalProvided > 0 ? goodInfo.Value.totalProvided : 0) + goodInfo.Value.extraProduced);
            float amountNeeded = YafcRounding((goodInfo.Value.totalProvided < 0 ? -goodInfo.Value.totalProvided : 0) + goodInfo.Value.totalNeeded);

            if (Math.Abs(amountAvailable - amountNeeded) < Epsilon || amountNeeded == 0) {
                continue;
            }

            yield return (goodInfo.Key, goodInfo.Value, amountAvailable - amountNeeded);
        }
    }

    public SummaryView() {
        tabColumn = new SummaryTabColumn();
        goodsColumn = new SummaryDataColumn(this);
        leftGrid = new DataGrid<ProjectPage>(tabColumn);
        rightGrid = new DataGrid<ProjectPage>(goodsColumn);
        // Only the right side draws the visible vertical scrollbar; the left's vertical scrolling is mirrored from
        // the right via Link() so both sides stay aligned without a second visible scrollbar.
        leftScrollArea = new LinkedScrollArea(DefaultScrollHeight, BuildLeftScrollArea, horizontal: false, drawVerticalScrollbar: false);
        rightScrollArea = new LinkedScrollArea(DefaultScrollHeight, BuildRightScrollArea, horizontal: true, drawVerticalScrollbar: true);
        leftScrollArea.Link(rightScrollArea);
    }

    [MemberNotNull(nameof(project))]
    public void SetProject(Project project) {
        if (ReferenceEquals(this.project, project)) {
            SyncPageSubscriptions();
            Recalculate();
            return;
        }

        if (this.project != null) {
            this.project.metaInfoChanged -= ProjectMetaInfoChanged;
            UnsubscribeFromAllPages();
        }

        this.project = project;
        project.metaInfoChanged += ProjectMetaInfoChanged;
        SyncPageSubscriptions();

        Recalculate();
    }

    private void ProjectMetaInfoChanged() {
        SyncPageSubscriptions();
        Recalculate();
    }

    private void SyncPageSubscriptions() {
        foreach (ProjectPage page in subscribedPages.Where(page => !project.pages.Contains(page)).ToList()) {
            page.contentChanged -= Recalculate;
            _ = subscribedPages.Remove(page);
        }

        foreach (ProjectPage page in project.pages) {
            if (subscribedPages.Add(page)) {
                page.contentChanged += Recalculate;
            }
        }
    }

    private void UnsubscribeFromAllPages() {
        foreach (ProjectPage page in subscribedPages) {
            page.contentChanged -= Recalculate;
        }

        subscribedPages.Clear();
    }

    protected override void BuildPageTooltip(ImGui gui, Summary contents) {
    }

    protected override void BuildHeader(ImGui gui) {
        base.BuildHeader(gui);

        gui.allocator = RectAllocator.Center;
        gui.BuildText(LSs.SummaryHeader, new TextBlockDisplayStyle(Font.header, Alignment: RectAlignment.Middle));
        gui.allocator = RectAllocator.LeftAlign;
    }

    private float CalculateFirstColumWidth(ImGui gui) {
        // At the least the icons should fit
        float width = FirstColumnIconSize;

        foreach (Guid displayPage in project.displayPages) {
            ProjectPage? page = project.FindPage(displayPage);

            if (page?.contentType != typeof(ProductionTable)) {
                continue;
            }

            Vector2 textSize = gui.GetTextDimensions(out _, page.name);
            width = MathF.Max(width, textSize.X);
        }

        // Include padding to perfectly match
        return width + FirstColumnPadding.left + FirstColumnPadding.right;
    }

    protected override async void BuildContent(ImGui gui) {
        using (gui.EnterRow()) {
            _ = gui.AllocateRect(0, 2); // Increase row height to 2, for vertical centering.

            if (gui.BuildCheckBox(LSs.SummaryOnlyShowIssues, model?.showOnlyIssues ?? false, out bool newValue)) {
                model!.showOnlyIssues = newValue; // null-forgiving: when model is null, the page is no longer being displayed, so no clicks can happen.
                Recalculate();
            }

            using (gui.EnterRowWithHelpIcon(LSs.SummaryAutoBalanceHint, false)) {
                if (gui.BuildButton(LSs.SummaryAutoBalance)) {
                    await AutoBalance();
                }
            }

            if (cubeOccupancy > Epsilon) {
                using (gui.EnterRowWithHelpIcon(LSs.SummaryCubeOccupancyHint, false)) {
                    gui.BuildText($"{LSs.SummaryCubeOccupancy.L()}: {DataUtils.FormatAmount(cubeOccupancy, UnitOfMeasure.Percent)}");
                }
            }
        }
        gui.AllocateSpacing();

        if (gui.isBuilding) {
            firstColumnWidth = CalculateFirstColumWidth(gui);
            rowHeight = ButtonDisplayStyle.ProductionTableUnscaled.Size + gui.PixelsToUnits(gui.GetFontSize().lineSize);
        }

        // Lay out left (sticky) and right (horizontally scrollable) side-by-side. The left side's scrollbar is
        // hidden, so it doesn't reserve scrollbar width. DataGrid.BuildRow shifts the column by `innerPadding.left`
        // twice (once via the row's starting x, once via the manual-rect padding) before reserving `innerPadding.right`
        // at the right edge — so account for both inner-padding halves here.
        using (gui.EnterRow(spacing: 0, allocator: RectAllocator.LeftRow)) {
            // Compute availableHeight *inside* the row so EnterRow's implicit spacing is already accounted for in
            // gui.statePosition.Y. visibleSize.Y - headerHeight is the body's viewport; subtracting the current top
            // gives the exact remaining height. Fall back to DefaultScrollHeight on the first build, before
            // ProjectPageView.Build has populated visibleSize.
            float availableHeight = MathF.Max(DefaultScrollHeight, visibleSize.Y - headerHeight - gui.statePosition.Y);
            float leftWidth = firstColumnWidth + (2 * DataGrid<ProjectPage>.innerPadding.left) + DataGrid<ProjectPage>.innerPadding.right;
            using (gui.EnterFixedPositioning(leftWidth, 0, default)) {
                leftScrollArea.Build(gui, availableHeight);
            }
            using (gui.EnterFixedPositioning(gui.width, 0, default)) {
                rightScrollArea.Build(gui, availableHeight);
            }
        }
    }

    // SetProject can recalculate before SetModel attaches the persisted showOnlyIssues value, which affects column width.
    protected override void ModelContentsChanged(bool visualOnly) => Recalculate(visualOnly);

    /// <summary>Maximum total time in milliseconds that Auto balance is allowed to keep re-running.</summary>
    private const long AutoBalanceTimeoutMs = 1000;

    private async Task AutoBalance() {
        // Keep running passes until the state stops changing (stable), or the overall budget runs out.
        // Use a monotonic wall clock: this whole loop can run synchronously inside one main-loop tick, so Ui.time (which is
        // only updated in ProcessEvents) may never advance and the deadline would never be reached.
        long deadline = Environment.TickCount64 + AutoBalanceTimeoutMs;
        while (true) {
            // A pass that updates no links can still change the state (its final forced re-solve of the displayed pages can
            // update them, which may create new work), so stability means the state itself is unchanged, not that the pass
            // didn't touch any link.
            Dictionary<string, GoodDetails> before = allGoods;
            await AutoBalancePass(deadline);
            if (SameGoods(before, allGoods) || Environment.TickCount64 >= deadline) {
                break;
            }
        }
    }

    private static bool SameGoods(Dictionary<string, GoodDetails> a, Dictionary<string, GoodDetails> b) {
        if (a.Count != b.Count) {
            return false;
        }
        foreach (KeyValuePair<string, GoodDetails> entry in a) {
            if (!b.TryGetValue(entry.Key, out GoodDetails value) || !EqualityComparer<GoodDetails>.Default.Equals(entry.Value, value)) {
                return false;
            }
        }
        return true;
    }

    private async Task AutoBalancePass(long deadline) {
        try {
            // The things we've already updated, and the error each had before their previous update.
            Queue<ProductionLink> updateOrder = [];
            Dictionary<ProductionLink, float> previousUpdates = [];
            int negativeFeedbackCount = 0;

            for (int i = 0; i < 1000; i++) { // No matter what else happens, give up after 1000 clicks.
                if (Environment.TickCount64 >= deadline) {
                    break;
                }
                float? oldExcess = null;
                GoodDetails details;
                float excess;
                ProductionLink? link;

                while (true) {
                    // Look for a product that (a) has a mismatch, (b) has exactly one link in the displayed pages, and (c) hasn't been updated yet.
                    (details, excess, link) = GoodsToBalance()
                        // Find the link
                        .Select(x => (x.details, x.excess, link: DisplayTables.Select(t => t.links.FirstOrDefault(l => l.goods.QualityName() == x.name && l.amount != 0)).WhereNotNull().SingleOrDefault(false)))
                        // Find an item with exactly one link that hasn't been updated yet. (Or has been removed to allow it to be updated again.)
                        // Never pick a link that is already at its target amount: 'updating' it is a no-op that only wastes the
                        // negative-feedback budget and pokes the solve state for no reason.
                        .FirstOrDefault(x => x.link != null && !previousUpdates.ContainsKey(x.link) && Math.Abs(x.details.sum - YafcRounding(x.link.amount)) >= Epsilon);

                    if (link != null) { break; }

                    if (updateOrder.Count == 0) {
                        return;
                    }
                    // If nothing was found, allow the least-recently updated product to be updated again, but remember its previous state so we can tell if we're making progress.
                    ProductionLink removedLink = updateOrder.Dequeue();
                    oldExcess = previousUpdates[removedLink];
                    _ = previousUpdates.Remove(removedLink);
                }

                if (oldExcess - Math.Abs(excess) <= Epsilon) {
                    // TODO: The negative feedback detection sometimes gets triggered, but sometimes the `updateOrder.Count == 0` check gets triggered instead.
                    // This probably needs to be revisited when fixing https://github.com/Yafc-CE/yafc-ce/issues/169
                    // The previous positive difference for this product is less than the current (supposedly better) positive difference.
                    if (++negativeFeedbackCount > 3) {
                        return; // Too many negative feedbacks detected; give up.
                    }
                    else { // Skip this for now, in hopes of getting a better result later.
                        updateOrder.Enqueue(link);
                        previousUpdates[link] = Math.Abs(excess);
                        continue;
                    }
                }

                updateOrder.Enqueue(link);
                previousUpdates[link] = Math.Abs(excess);

                await SummaryDataColumn.SetProviderAmount(link, (ProjectPage)link.owner.owner, details.sum);
            }
            _ = model.showOnlyIssues;
        }
        finally {
            // HACK: Nothing left to change, but force a recalculate anyway, so the user doesn't have to view all the tabs before clicking Auto balance again.
            // Sometimes this will cause updates that require an additional pass.
            // This is probably related to https://github.com/Yafc-CE/yafc-ce/issues/169
            //
            // The link updates made above are still pending in the undo batch: the app commits them only when the current
            // gesture ends, which happens AFTER this whole loop. Until the commit, the touched pages don't even register as
            // stale, so nothing gets solved and the stability check would measure the pre-update state — which is exactly
            // why every follow-up round only happened on the user's next click. So settle the state now, in a loop:
            //   1. FlushPendingChanges() commits the pending updates; that makes the touched pages stale and kicks off
            //      background solves for the active ones.
            //   2. A yield lets those solves run their main-thread continuations (the solver runs on a worker thread).
            //   3. The sweep resolves any page that is (still) stale; solving one page can make another page stale again.
            // Repeat until a round changes nothing. Each pass still commits as a single undo batch, so Ctrl-Z granularity
            // is preserved.
            bool settled = false;
            while (!settled && Environment.TickCount64 < deadline) {
                Dictionary<string, GoodDetails> before = allGoods;
                project.undo.FlushPendingChanges();
                await Task.Yield(); // let solves kicked off by the commit (or the sweep below) run their foreground continuations
                bool resolvedAny = false;
                foreach (ProjectPage page in DisplayPages) {
                    if (page.IsSolutionStale()) {
                        resolvedAny = true;
                        await page.RunSolveJob();
                    }
                }
                settled = !resolvedAny && SameGoods(before, allGoods);
            }
        }
    }

    /// <summary>
    /// The item names of the singleton Ultradense Utility Cube from the Ultracube mod, in both its active and dormant forms.
    /// </summary>
    private static readonly HashSet<string> CubeNames = ["cube-ultradense-utility-cube", "cube-dormant-utility-cube"];

    private static bool RowUsesCube(RecipeRow row) =>
        row.fuel is not null && CubeNames.Contains(row.fuel.target.name)
        || (row.recipe?.target.ingredients.Any(i => CubeNames.Contains(i.goods.name)) ?? false);

    private IEnumerable<ProductionTable> DisplayTables => project.displayPages.Select(p => project.FindPage(p)?.content as ProductionTable).WhereNotNull();
    private IEnumerable<ProjectPage> DisplayPages => DisplayTables.Select(t => (ProjectPage)t.owner);

    private void BuildLeftScrollArea(ImGui gui) {
        foreach (ProjectPage page in DisplayPages) {
            _ = leftGrid.BuildRow(gui, page, minRowHeight: rowHeight);
        }
        // Match the right side's reserved-for-H-scrollbar space so the synced vertical scroll positions align
        // perfectly between the two sides.
        _ = gui.AllocateRect(0, ScrollArea.ScrollbarSize);
    }

    private void BuildRightScrollArea(ImGui gui) {
        foreach (ProjectPage page in DisplayPages) {
            _ = rightGrid.BuildRow(gui, page, minRowHeight: rowHeight);
        }
        // Reserve space below the last row so it can be scrolled clear of the horizontal scrollbar that sits at
        // the bottom of this scroll area.
        _ = gui.AllocateRect(0, ScrollArea.ScrollbarSize);
    }

    private void Recalculate() => Recalculate(false);

    private void Recalculate(bool visualOnly) {
        Dictionary<string, GoodDetails> newGoods = [];

        foreach (Guid displayPage in project.displayPages) {
            ProjectPage? page = project.FindPage(displayPage);
            ProductionTable? content = page?.content as ProductionTable;

            if (content == null) {
                continue;
            }

            foreach (IProductionLink link in content.allLinks) {
                if (link.amount != 0f) {
                    GoodDetails value = newGoods.GetValueOrDefault(link.goods.QualityName());
                    value.totalProvided += YafcRounding(link.amount); ;
                    newGoods[link.goods.QualityName()] = value;
                }
            }

            foreach (ProductionTableFlow flow in content.flow) {
                if (flow.amount < -Epsilon) {
                    GoodDetails value = newGoods.GetValueOrDefault(flow.goods.QualityName());
                    value.totalNeeded -= YafcRounding(flow.amount); ;
                    value.sum -= YafcRounding(flow.amount); ;
                    newGoods[flow.goods.QualityName()] = value;
                }
                else if (flow.amount > Epsilon) {
                    if (!content.allLinks.Any(x => x.goods == flow.goods)) {
                        // Only count extras if not linked
                        GoodDetails value = newGoods.GetValueOrDefault(flow.goods.QualityName());
                        value.extraProduced += YafcRounding(flow.amount);
                        value.sum -= YafcRounding(flow.amount);
                        newGoods[flow.goods.QualityName()] = value;
                    }
                }
            }
        }

        cubeOccupancy = 0f;
        foreach (ProductionTable table in DisplayTables) {
            foreach (RecipeRow row in table.GetAllRecipes()) {
                if (row.hierarchyEnabled && RowUsesCube(row)) {
                    cubeOccupancy += row.buildingCount;
                }
            }
        }

        int count = 0;
        foreach (KeyValuePair<string, GoodDetails> entry in newGoods) {
            float amountAvailable = YafcRounding((entry.Value.totalProvided > 0 ? entry.Value.totalProvided : 0) + entry.Value.extraProduced);
            float amountNeeded = YafcRounding((entry.Value.totalProvided < 0 ? -entry.Value.totalProvided : 0) + entry.Value.totalNeeded);

            if (model != null && model.showOnlyIssues && (Math.Abs(amountAvailable - amountNeeded) < Epsilon || amountNeeded == 0)) {
                continue;
            }
            count++;
        }

        // Width of the goods column: N cells of (ElementWidth + ElementSpacing), minus the trailing inter-cell
        // spacing after the last cell, which doesn't visually exist.
        goodsColumn.width = (count * (ElementWidth + ElementSpacing)) - ElementSpacing;

        allGoods = newGoods;

        Rebuild(visualOnly);
        leftScrollArea.RebuildContents();
        rightScrollArea.RebuildContents();
    }

    // Convert/truncate value as shown in UI to prevent slight mismatches
    private static float YafcRounding(float value) {
        _ = DataUtils.TryParseAmount(DataUtils.FormatAmount(value, UnitOfMeasure.PerSecond), out float result, UnitOfMeasure.PerSecond);
        return result;
    }

    public override void SetSearchQuery(SearchQuery query) {
        searchQuery = query;
        bodyContent.Rebuild();
        leftScrollArea.Rebuild();
        rightScrollArea.Rebuild();
    }
}
