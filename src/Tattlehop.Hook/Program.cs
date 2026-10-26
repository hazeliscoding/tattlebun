using Tattlehop.Hook;
using Tattlehop.Windows;

if (args is ["--list"])
{
    if (!File.Exists(ReceiptStore.DefaultPath))
    {
        Console.Write("No receipts yet." + Environment.NewLine);
        return 0;
    }

    using var store = new ReceiptStore(ReceiptStore.DefaultPath);
    Console.Write(ReceiptList.Format(store.Commands(), store.Tools(), store.Verdicts()) + ReceiptList.Timing(store.HookTimes()));
    return 0;
}

return HookRun.Run(Console.OpenStandardInput(), ReceiptStore.DefaultPath, (fingerprinter, previous, written) => new SnapshotReader(fingerprinter).Read(previous, written));
