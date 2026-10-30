using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using L22.Editor.Models.Ipc;

namespace L22.Editor.Views;

public partial class PinApprovalDialog : Window
{
    public ApprovalDecision Result { get; private set; } = ApprovalDecision.Block;
    public string GeneratedPin { get; }

    public PinApprovalDialog() : this("(unknown)", "(unknown)") { }

    public PinApprovalDialog(string fileId, string agent)
    {
        InitializeComponent();
        GeneratedPin = GenerateSixDigitPin();
        LblFile.Text = $"File: {fileId}";
        LblAgent.Text = $"Agent: {agent}";
        LblPin.Text = GeneratedPin;
    }

    private static string GenerateSixDigitPin()
    {
        var rng = new Random();
        return rng.Next(0, 1000000).ToString("D6");
    }

    private void OnBlock(object? s, RoutedEventArgs e)   { Result = ApprovalDecision.Block;                Close(); }
    private void OnAllowOne(object? s, RoutedEventArgs e){ Result = ApprovalDecision.AllowOneHour;         Close(); }
    private void OnAllowDay(object? s, RoutedEventArgs e){ Result = ApprovalDecision.AllowTwentyFourHours; Close(); }
}
