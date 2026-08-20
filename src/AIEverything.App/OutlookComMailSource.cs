using System.Runtime.InteropServices;
using AIEverything.Desktop.Mail;

namespace AIEverything.App;

internal sealed class OutlookComMailSource : IMailSource
{
    private const int InboxFolder = 6;
    private const int SentMailFolder = 5;
    private const int MailItemClass = 43;

    public Task<MailReadBatch> ReadRecentAsync(int limit, CancellationToken cancellationToken) =>
        RunStaAsync(() => ReadRecent(limit, cancellationToken), cancellationToken);

    public Task OpenAsync(MailIdentity identity, CancellationToken cancellationToken) =>
        RunStaAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            object? application = null;
            object? session = null;
            object? item = null;
            try
            {
                application = CreateOutlookApplication();
                session = ((dynamic)application).GetNamespace("MAPI");
                item = ((dynamic)session).GetItemFromID(identity.EntryId, identity.StoreId);
                ((dynamic)item).Display(false);
                return true;
            }
            finally
            {
                Release(item);
                Release(session);
                Release(application);
            }
        }, cancellationToken);

    private static MailReadBatch ReadRecent(int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > MailSearchModule.MaximumMessages)
            throw new ArgumentOutOfRangeException(nameof(limit));

        object? application = null;
        object? session = null;
        try
        {
            application = CreateOutlookApplication();
            session = ((dynamic)application).GetNamespace("MAPI");
            var messages = new List<MailMessageSnapshot>(limit * 2);
            var skipped = 0;
            ReadFolder(session, InboxFolder, "收件箱", "[ReceivedTime]", limit,
                messages, ref skipped, cancellationToken);
            ReadFolder(session, SentMailFolder, "已发送", "[SentOn]", limit,
                messages, ref skipped, cancellationToken);
            return new MailReadBatch(
                messages.OrderByDescending(message => message.Timestamp).Take(limit).ToArray(),
                skipped);
        }
        finally
        {
            Release(session);
            Release(application);
        }
    }

    private static void ReadFolder(
        object session,
        int folderId,
        string folderLabel,
        string sortProperty,
        int limit,
        List<MailMessageSnapshot> messages,
        ref int skipped,
        CancellationToken cancellationToken)
    {
        object? folder = null;
        object? items = null;
        try
        {
            folder = ((dynamic)session).GetDefaultFolder(folderId);
            var storeId = Convert.ToString(((dynamic)folder).StoreID) ?? string.Empty;
            items = ((dynamic)folder).Items;
            ((dynamic)items).Sort(sortProperty, true);
            var count = Math.Min(Convert.ToInt32(((dynamic)items).Count), limit);
            for (var index = 1; index <= count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                object? item = null;
                try
                {
                    item = ((dynamic)items)[index];
                    if (Convert.ToInt32(((dynamic)item).Class) != MailItemClass)
                    {
                        continue;
                    }

                    var entryId = Convert.ToString(((dynamic)item).EntryID) ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(storeId) || string.IsNullOrWhiteSpace(entryId))
                    {
                        skipped++;
                        continue;
                    }

                    var timestampValue = Convert.ToDateTime(
                        folderId == SentMailFolder
                            ? ((dynamic)item).SentOn
                            : ((dynamic)item).ReceivedTime);
                    var timestamp = new DateTimeOffset(
                        DateTime.SpecifyKind(timestampValue, DateTimeKind.Local)).ToUniversalTime();
                    messages.Add(new MailMessageSnapshot(
                        new MailIdentity(storeId, entryId),
                        Text(((dynamic)item).Subject),
                        Text(((dynamic)item).SenderName),
                        BuildRecipients(item),
                        timestamp,
                        folderLabel,
                        Text(((dynamic)item).Body),
                        ReadAttachmentNames(item)));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    skipped++;
                }
                finally
                {
                    Release(item);
                }
            }
        }
        finally
        {
            Release(items);
            Release(folder);
        }
    }

    private static string BuildRecipients(object item)
    {
        var to = Text(((dynamic)item).To);
        var cc = Text(((dynamic)item).CC);
        return string.IsNullOrWhiteSpace(cc)
            ? to
            : string.IsNullOrWhiteSpace(to) ? cc : $"{to}; {cc}";
    }

    private static string ReadAttachmentNames(object item)
    {
        object? attachments = null;
        try
        {
            attachments = ((dynamic)item).Attachments;
            var names = new List<string>();
            var count = Convert.ToInt32(((dynamic)attachments).Count);
            for (var index = 1; index <= count; index++)
            {
                object? attachment = null;
                try
                {
                    attachment = ((dynamic)attachments)[index];
                    var name = Text(((dynamic)attachment).FileName);
                    if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
                }
                catch
                {
                }
                finally
                {
                    Release(attachment);
                }
            }

            return string.Join("; ", names);
        }
        finally
        {
            Release(attachments);
        }
    }

    private static object CreateOutlookApplication()
    {
        var type = Type.GetTypeFromProgID("Outlook.Application", throwOnError: false)
                   ?? throw new InvalidOperationException("未检测到 Classic Outlook。请确认已安装并完成本机配置。");
        return Activator.CreateInstance(type)
               ?? throw new InvalidOperationException("Classic Outlook 暂时无法启动。");
    }

    private static string Text(object? value) =>
        (Convert.ToString(value) ?? string.Empty).Replace('\0', ' ').Trim();

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try
            {
                Marshal.FinalReleaseComObject(value);
            }
            catch
            {
            }
        }
    }

    private static Task<T> RunStaAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(action());
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "AIEverything.Outlook"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
