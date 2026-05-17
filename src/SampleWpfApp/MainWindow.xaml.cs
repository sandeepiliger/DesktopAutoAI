using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace SampleWpfApp;

public partial class MainWindow : Window
{
    private const string AllStatuses = "All statuses";

    private readonly ObservableCollection<OrderRecord> _orders =
    [
        new("ORD-1042", "Aarav Textiles", "Mumbai", "New", DateTime.Today.AddDays(2), 18420.50m),
        new("ORD-1043", "BrightFoods Market", "Pune", "Processing", DateTime.Today.AddDays(4), 9275.00m),
        new("ORD-1044", "Cedar Clinic", "Bengaluru", "Shipped", DateTime.Today.AddDays(1), 34210.90m),
        new("ORD-1045", "Delta Motors", "Chennai", "Delayed", DateTime.Today.AddDays(6), 15200.00m),
        new("ORD-1046", "Evergreen Stores", "Delhi", "Processing", DateTime.Today.AddDays(3), 11875.25m),
        new("ORD-1047", "Fabrikam Labs", "Hyderabad", "New", DateTime.Today.AddDays(5), 22190.00m)
    ];

    private readonly ObservableCollection<InventoryRecord> _inventory =
    [
        new("SKU-1001", "Wireless scanner", "Mumbai A", 42, 15),
        new("SKU-1002", "Thermal label roll", "Pune B", 320, 80),
        new("SKU-1003", "Packing tape", "Delhi C", 64, 50),
        new("SKU-1004", "Industrial tablet", "Bengaluru A", 18, 10),
        new("SKU-1005", "Docking cradle", "Chennai B", 7, 12),
        new("SKU-1006", "Safety gloves", "Hyderabad C", 210, 75)
    ];

    private readonly ObservableCollection<TaskRecord> _tasks =
    [
        new("Confirm delayed shipment", "Anika Rao", "Open", DateTime.Today.AddDays(1).ToString("dd MMM")),
        new("Review warehouse variance", "Michael Chen", "In Review", DateTime.Today.AddDays(2).ToString("dd MMM")),
        new("Call premium customer", "Priya Shah", "Open", DateTime.Today.ToString("dd MMM")),
        new("Prepare weekly report", "Jordan Smith", "Scheduled", DateTime.Today.AddDays(3).ToString("dd MMM"))
    ];

    private List<OrderRecord> _visibleOrders = [];

    public MainWindow()
    {
        InitializeComponent();
        LoadSampleData();
    }

    private void LoadSampleData()
    {
        RefreshOrderGrid(_orders);
        InventoryDataGrid.ItemsSource = _inventory;
        TasksListView.ItemsSource = _tasks;
        AssignedToComboBox.SelectedIndex = 0;
        OrdersDataGrid.SelectedIndex = 0;
        UpdateDashboardCounts();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        SyncProgressBar.Value = 100;
        SearchTextBox.Text = string.Empty;
        StatusComboBox.SelectedIndex = 0;
        RefreshOrderGrid(_orders);
        UpdateDashboardCounts();
        SetStatus("Dashboard refreshed, filters reset, and all sample rows reloaded.");
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedOrder = GetSelectedOrder();
        if (selectedOrder is null)
        {
            SetStatus("Select an order before saving.");
            return;
        }

        ReplaceOrder(selectedOrder with
        {
            Customer = string.IsNullOrWhiteSpace(CustomerTextBox.Text) ? selectedOrder.Customer : CustomerTextBox.Text.Trim()
        });

        SetStatus($"Saved customer details for {CustomerTextBox.Text}.");
    }

    private void SignOutButton_Click(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        SignOutButton.Content = "Signed Out";
        SetStatus("Signed out. Save and refresh are disabled for this sample session.");
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        var searchText = SearchTextBox.Text.Trim();
        var status = GetSelectedStatus();

        var filteredOrders = _orders.Where(order =>
            (string.IsNullOrWhiteSpace(searchText)
             || order.OrderId.Contains(searchText, StringComparison.OrdinalIgnoreCase)
             || order.Customer.Contains(searchText, StringComparison.OrdinalIgnoreCase)
             || order.City.Contains(searchText, StringComparison.OrdinalIgnoreCase))
            && (status == AllStatuses || order.Status.Equals(status, StringComparison.OrdinalIgnoreCase)));

        RefreshOrderGrid(filteredOrders);
        SetStatus($"Search applied. {OrdersDataGrid.Items.Count} matching order(s) displayed.");
    }

    private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
    {
        SearchTextBox.Text = string.Empty;
        StatusComboBox.SelectedIndex = 0;
        RefreshOrderGrid(_orders);
        SetStatus("Search filters cleared.");
    }

    private void OrdersDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OrdersDataGrid.SelectedItem is not OrderRecord order)
        {
            return;
        }

        CustomerTextBox.Text = order.Customer;
        NotesTextBox.Text = $"{order.OrderId} is currently {order.Status.ToLowerInvariant()} for {order.City}.";
        SelectedOrderTextBlock.Text = $"Selected order: {order.OrderId}";
        SetStatus($"Selected {order.OrderId} for {order.Customer}.");
    }

    private void ApproveButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateSelectedOrderStatus("Approved", $"Approved selected order for {CustomerTextBox.Text}.");
    }

    private void HoldButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateSelectedOrderStatus("On Hold", $"Placed selected order for {CustomerTextBox.Text} on hold.");
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateSelectedOrderStatus("Cancelled", $"Cancelled selected order for {CustomerTextBox.Text}.");
    }

    private void SidebarButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            switch (button.Content?.ToString())
            {
                case "Inventory":
                    MainTabControl.SelectedIndex = 0;
                    break;
                case "Reports":
                case "Orders":
                case "Customers":
                    MainTabControl.SelectedIndex = 1;
                    break;
                case "Dashboard":
                    MainTabControl.SelectedIndex = 0;
                    OrdersDataGrid.ScrollIntoView(OrdersDataGrid.SelectedItem);
                    break;
            }

            SetStatus($"{button.Content} section selected from sidebar.");
        }
    }

    private void OptionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not CheckBox checkBox)
        {
            return;
        }

        var state = checkBox.IsChecked == true ? "enabled" : "disabled";
        SetStatus($"{checkBox.Content} {state}.");
    }

    private void AddTaskButton_Click(object sender, RoutedEventArgs e)
    {
        var title = string.IsNullOrWhiteSpace(NewTaskTextBox.Text)
            ? "Untitled follow-up"
            : NewTaskTextBox.Text.Trim();

        _tasks.Add(new TaskRecord(title, "Anika Rao", "Open", DateTime.Today.AddDays(1).ToString("dd MMM")));
        MainTabControl.SelectedIndex = 1;
        TasksListView.SelectedIndex = _tasks.Count - 1;
        SetStatus($"Added task '{title}'.");
    }

    private void MarkTaskDoneButton_Click(object sender, RoutedEventArgs e)
    {
        if (TasksListView.SelectedItem is TaskRecord selectedTask)
        {
            var index = _tasks.IndexOf(selectedTask);
            _tasks[index] = selectedTask with { State = "Done" };
            TasksListView.SelectedIndex = index;
            SetStatus($"Marked task '{selectedTask.Title}' as done.");
            return;
        }

        SetStatus("Select a task before marking it done.");
    }

    private void RemoveTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (TasksListView.SelectedItem is TaskRecord selectedTask)
        {
            _tasks.Remove(selectedTask);
            TasksListView.SelectedIndex = _tasks.Count > 0 ? 0 : -1;
            SetStatus($"Removed task '{selectedTask.Title}'.");
            return;
        }

        SetStatus("Select a task before removing it.");
    }

    private void NewOrderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var nextOrder = new OrderRecord($"ORD-{1042 + _orders.Count}", "New Sample Customer", "Kolkata", "New", DateTime.Today.AddDays(7), 6400m);
        _orders.Add(nextOrder);
        RefreshOrderGrid(_orders);
        OrdersDataGrid.SelectedItem = nextOrder;
        UpdateDashboardCounts();
        SetStatus($"Created {nextOrder.OrderId} from the File menu.");
    }

    private void ExportReportMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var exportPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "SampleWpfApp-OrderReport.csv");

        var reportLines = new[]
        {
            "OrderId,Customer,City,Status,DueDate,Total"
        }.Concat(_orders.Select(order =>
            $"{order.OrderId},{EscapeCsv(order.Customer)},{order.City},{order.Status},{order.DueDate:yyyy-MM-dd},{order.Total:F2}"));

        File.WriteAllLines(exportPath, reportLines);
        SetStatus($"Exported order report to {exportPath}.");
    }

    private void DashboardMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SetStatus("Dashboard view selected from menu.");
    }

    private void InventoryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        MainTabControl.SelectedIndex = 0;
        InventoryDataGrid.SelectedIndex = 0;
        SetStatus("Inventory tab selected from menu.");
    }

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            this,
            "Sample Operations Console\nA realistic WPF target for desktop automation testing.",
            "About Sample App",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SetStatus(string message)
    {
        StatusTextBlock.Text = $"{DateTime.Now:hh:mm:ss tt} - {message}";
    }

    private void RefreshOrderGrid(IEnumerable<OrderRecord> orders)
    {
        _visibleOrders = orders.ToList();
        OrdersDataGrid.ItemsSource = _visibleOrders;
        FilterResultTextBlock.Text = $"Showing {_visibleOrders.Count} of {_orders.Count} orders";

        if (_visibleOrders.Count > 0 && OrdersDataGrid.SelectedItem is null)
        {
            OrdersDataGrid.SelectedIndex = 0;
        }
    }

    private string GetSelectedStatus()
    {
        return (StatusComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? AllStatuses;
    }

    private OrderRecord? GetSelectedOrder()
    {
        return OrdersDataGrid.SelectedItem as OrderRecord;
    }

    private void UpdateSelectedOrderStatus(string status, string successMessage)
    {
        var selectedOrder = GetSelectedOrder();
        if (selectedOrder is null)
        {
            SetStatus("Select an order before changing status.");
            return;
        }

        ReplaceOrder(selectedOrder with { Status = status });
        UpdateDashboardCounts();
        SetStatus(successMessage);
    }

    private void ReplaceOrder(OrderRecord updatedOrder)
    {
        var orderIndex = _orders.ToList().FindIndex(order => order.OrderId == updatedOrder.OrderId);
        if (orderIndex < 0)
        {
            return;
        }

        _orders[orderIndex] = updatedOrder;
        SearchButton_Click(this, new RoutedEventArgs());
        OrdersDataGrid.SelectedItem = _visibleOrders.FirstOrDefault(order => order.OrderId == updatedOrder.OrderId);
    }

    private void UpdateDashboardCounts()
    {
        var openOrderCount = _orders.Count(order => order.Status is "New" or "Processing" or "Delayed" or "On Hold");
        OpenOrdersTextBlock.Text = openOrderCount.ToString();
    }

    private static string EscapeCsv(string value)
    {
        return value.Contains(',') ? $"\"{value}\"" : value;
    }
}

public sealed record OrderRecord(string OrderId, string Customer, string City, string Status, DateTime DueDate, decimal Total);

public sealed record InventoryRecord(string Sku, string ItemName, string Warehouse, int OnHand, int ReorderAt);

public sealed record TaskRecord(string Title, string Owner, string State, string Due);
