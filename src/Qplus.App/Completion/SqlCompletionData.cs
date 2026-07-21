using System.Windows.Media;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;

namespace Qplus.App.Completion;

/// <summary>One entry in the SQL completion popup.</summary>
public sealed class SqlCompletionData : ICompletionData
{
    public SqlCompletionData(string text, string description, double priority = 0)
    {
        Text = text;
        Description = description;
        Priority = priority;
    }

    /// <summary>Text inserted into the document (replaces what the user typed).</summary>
    public string Text { get; }

    /// <summary>What the popup row shows.</summary>
    public object Content => Text;

    public object Description { get; }

    public ImageSource? Image => null;

    public double Priority { get; }

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        => textArea.Document.Replace(completionSegment, Text);
}
