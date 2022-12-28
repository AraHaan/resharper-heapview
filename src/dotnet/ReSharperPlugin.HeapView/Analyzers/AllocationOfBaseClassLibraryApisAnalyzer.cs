#nullable enable
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Psi.CSharp.Tree;

namespace ReSharperPlugin.HeapView.Analyzers;

// [ElementProblemAnalyzer(
//   ElementTypes: new[] { typeof(IArrayInitializer) },
//   HighlightingTypes = new[] { typeof(ObjectAllocationHighlighting) })]
public class AllocationOfBaseClassLibraryApisAnalyzer : HeapAllocationAnalyzerBase<IInvocationExpression>
{
  protected override void Run(
    IInvocationExpression invocationExpression, ElementProblemAnalyzerData data, IHighlightingConsumer consumer)
  {

  }

  void InitMap()
  {
    Add<string>(nameof(string.Insert));
    Add<string>(nameof(string.Concat));
    Add<string>(nameof(string.Copy));
    Add<string>(nameof(string.Format));
    Add<string>(nameof(string.ToLowerInvariant));
    Add<string>(nameof(string.ToUpperInvariant));

    void Add<T>(string memberName) { }
  }

  //InterruptibleLazy<>


}