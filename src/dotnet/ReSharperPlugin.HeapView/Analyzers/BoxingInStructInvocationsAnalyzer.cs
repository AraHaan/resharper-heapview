using System;
using System.Text;
using JetBrains.Annotations;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.CSharp.Util;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.ReSharper.Psi.Util;
using JetBrains.UI.RichText;
using ReSharperPlugin.HeapView.Highlightings;
using ReSharperPlugin.HeapView.Settings;

namespace ReSharperPlugin.HeapView.Analyzers;

[ElementProblemAnalyzer(
  ElementTypes: [ typeof(IInvocationExpression), typeof(IReferenceExpression) ],
  HighlightingTypes = [
    typeof(BoxingAllocationHighlighting),
    typeof(PossibleBoxingAllocationHighlighting)
  ])]
public class BoxingInStructInvocationsAnalyzer : HeapAllocationAnalyzerBase<ICSharpExpression>
{
  protected override void Run(ICSharpExpression expression, ElementProblemAnalyzerData data, IHighlightingConsumer consumer)
  {
    switch (expression)
    {
      // structWithNoToString.ToString()
      case IInvocationExpression invocationExpression:
        CheckInheritedMethodInvocationOverValueType(invocationExpression, data, consumer);
        break;

      // Action a = structValue.InstanceMethod;
      case IReferenceExpression referenceExpression:
        CheckStructMethodConversionToDelegateInstance(referenceExpression, consumer);
        break;
    }
  }

  private static void CheckInheritedMethodInvocationOverValueType(
    IInvocationExpression invocationExpression, ElementProblemAnalyzerData data, IHighlightingConsumer consumer)
  {
    var invokedReferenceExpression = invocationExpression.InvokedExpression.GetOperandThroughParenthesis() as IReferenceExpression;
    if (invokedReferenceExpression == null) return;

    var (declaredElement, _, resolveErrorType) = invocationExpression.InvocationExpressionReference.Resolve();
    if (!resolveErrorType.IsAcceptable) return;

    var method = declaredElement as IMethod;
    if (method == null) return;

    if (method.IsStatic) return; // we are only insterested in instance methods

    var containingType = method.ContainingType;

    switch (method.ShortName)
    {
      case nameof(GetHashCode):
      case nameof(Equals):
      case nameof(ToString):
      case nameof(Enum.HasFlag) when containingType.IsSystemEnumClass():
      {
        CheckValueTypeVirtualMethodInvocation();
        return;
      }

      case nameof(GetType) when containingType.IsSystemObject():
      {
        CheckGetTypeMethodInvocation();
        return;
      }
    }

    return;

    void CheckGetTypeMethodInvocation()
    {
      var qualifierType = TryGetQualifierExpressionType(invokedReferenceExpression);
      if (qualifierType == null)
        return;

      if (!qualifierType.IsTypeBoxable())
        return; // erroneous invocation

      if (invokedReferenceExpression.IsInTheContextWhereAllocationsAreNotImportant())
        return;

      if (data.AnalyzeCodeLikeIfOptimizationsAreEnabled())
        return; // only allocates in DEBUG builds

      if (qualifierType.IsValueType())
      {
        consumer.AddHighlighting(new BoxingAllocationHighlighting(
          invokedReferenceExpression.NameIdentifier, new RichText(
            $"special '{GetObjectGetTypeInvocationText()}' method invocation over the value type instance")));
      }
      else if (qualifierType.IsUnconstrainedGenericType(out var typeParameter))
      {
        var typeParameterName = DeclaredElementPresenter.Format(
          invokedReferenceExpression.Language, DeclaredElementPresenter.NAME_PRESENTER, typeParameter);

        consumer.AddHighlighting(new PossibleBoxingAllocationHighlighting(
          invokedReferenceExpression.NameIdentifier, new RichText(
            $"special '{GetObjectGetTypeInvocationText()}' method may be invoked over the value type instance "
            + $"if '{typeParameterName}' type parameter will be substituted with the value type")));
      }

      return;

      RichText GetObjectGetTypeInvocationText()
      {
        var typeStyle = DeclaredElementPresenterTextStyles.Generic[DeclaredElementPresentationPartKind.Type];
        var methodStyle = DeclaredElementPresenterTextStyles.Generic[DeclaredElementPresentationPartKind.Method];

        return new RichText()
          .Append(nameof(Object), typeStyle)
          .Append('.')
          .Append(nameof(GetType), methodStyle)
          .Append("()");
      }
    }

    void CheckValueTypeVirtualMethodInvocation()
    {
      bool mustCheckOverride;

      switch (containingType)
      {
        // we've found non-overriden Equals/GetHashCode/ToString invoked over something
        case IClass classType when classType.IsSystemValueTypeClass()
                                   || classType.IsSystemEnumClass()
                                   || classType.IsSystemObject():
        {
          mustCheckOverride = false;
          break;
        }

        // Nullable<T> overrides Equals/GetHashCode/ToString, but invokes the corresponding methods on T
        case IStruct structType when structType.IsNullableOfT():
        {
          mustCheckOverride = true;
          break;
        }

        default: return;
      }

      var qualifierType = TryGetQualifierExpressionType(invokedReferenceExpression).Unlift();
      if (qualifierType == null) return;

      if (!qualifierType.IsTypeBoxable())
        return; // errorneous invocation

      if (mustCheckOverride && CheckHasVirtualMethodOverride(qualifierType.GetTypeElement()))
        return;

      if (invokedReferenceExpression.IsInTheContextWhereAllocationsAreNotImportant())
        return;

      if (IsStructVirtualMethodInvocationOptimizedAtRuntime(invocationExpression, method, qualifierType, data))
        return;

      if (qualifierType.IsTypeParameterType(out var typeParameter))
      {
        if (!qualifierType.IsReferenceType())
        {
          var richText = new RichText();
          richText.Append("inherited '");
          richText.Append(PresentMethod());
          richText.Append("' virtual method invocation over the value type instance if '");
          richText.Append(DeclaredElementPresenter.Format(
            CSharpLanguage.Instance!, DeclaredElementPresenter.NAME_PRESENTER, typeParameter));
          richText.Append("' type parameter will be substituted with the value type ");
          richText.Append("that do not overrides '");
          richText.Append(DeclaredElementPresenter.Format(
            CSharpLanguage.Instance!, DeclaredElementPresenter.NAME_PRESENTER, method));
          richText.Append("' virtual method");

          consumer.AddHighlighting(
            new PossibleBoxingAllocationHighlighting(
              invokedReferenceExpression.NameIdentifier, richText));
        }
      }
      else if (qualifierType.IsValueType())
      {
        consumer.AddHighlighting(new BoxingAllocationHighlighting(
          invokedReferenceExpression.NameIdentifier, new RichText(
            $"inherited '{PresentMethod()}' virtual method invocation over the value type instance")));
      }

      return;

      [Pure]
      bool CheckHasVirtualMethodOverride(ITypeElement? typeElement)
      {
        switch (typeElement)
        {
          // Nullable<T> overrides won't help us to detect if the corresponding method in T
          // is overriden or not, so we have to do the override check manually
          case IStruct structType:
            return StructOverridesChecker.IsMethodOverridenInStruct(structType, method.ShortName, data);
          case IEnum:
            return false; // enums do not have virtual method overrides
          case ITypeParameter:
            return false; // in generic code we are not assuming any overrides
          default:
            return true; // something weird is found under Nullable<T>
        }
      }

      string PresentMethod()
      {
        // present the actual cause of the boxing inside Nullable<T>.GetHashCode/Equals/ToString
        if (containingType.IsNullableOfT())
        {
          var unliftedTypeElement = qualifierType.GetTypeElement();
          if (unliftedTypeElement != null)
          {
            foreach (var superTypeElement in unliftedTypeElement.GetSuperTypeElements())
            {
              if (superTypeElement is IClass)
              {
                containingType = superTypeElement;
                break;
              }
            }
          }
        }

        return $"{containingType.ShortName}.{method.ShortName}()";
      }
    }
  }

  private static void CheckStructMethodConversionToDelegateInstance(
    IReferenceExpression referenceExpression, IHighlightingConsumer consumer)
  {
    var invocationExpression = InvocationExpressionNavigator.GetByInvokedExpression(referenceExpression.GetContainingParenthesizedExpression());
    if (invocationExpression != null) return;

    if (referenceExpression.IsNameofOperatorTopArgument()) return;

    var (declaredElement, _, resolveErrorType) = referenceExpression.Reference.Resolve();
    if (!resolveErrorType.IsAcceptable) return;

    var method = declaredElement as IMethod;
    if (method == null) return;

    if (method.IsStatic) return;

    var qualifierType = TryGetQualifierExpressionType(referenceExpression);
    if (qualifierType == null) return;

    if (qualifierType.IsReferenceType()) return;

    var delegateType = referenceExpression.TryFindTargetDelegateType();
    if (delegateType == null) return;

    if (referenceExpression.IsInTheContextWhereAllocationsAreNotImportant())
      return;

    var language = referenceExpression.Language;
    var sourceTypeText = qualifierType.GetPresentableName(language, CommonUtils.DefaultTypePresentationStyle);
    var delegateTypeText = delegateType.GetPresentableName(language, CommonUtils.DefaultTypePresentationStyle);

    var nodeToHighlight = referenceExpression.QualifierExpression
                          ?? (ITreeNode) referenceExpression.NameIdentifier;

    if (qualifierType.IsUnconstrainedGenericType(out var typeParameter))
    {
      var typeParameterName = DeclaredElementPresenter.Format(
        CSharpLanguage.Instance!, DeclaredElementPresenter.NAME_PRESENTER, typeParameter);

      consumer.AddHighlighting(new PossibleBoxingAllocationHighlighting(
        nodeToHighlight, new RichText(
          $"conversion of value type '{sourceTypeText}' instance method to '{delegateTypeText}' delegate type"
          + $" if '{typeParameterName}' type parameter will be substituted with the value type")));
    }
    else
    {
      consumer.AddHighlighting(new BoxingAllocationHighlighting(
        nodeToHighlight, new RichText(
          $"conversion of value type '{sourceTypeText}' instance method to '{delegateTypeText}' delegate type")));
    }
  }

  [Pure]
  private static IType? TryGetQualifierExpressionType(IReferenceExpression referenceExpression)
  {
    var qualifierExpression = referenceExpression.QualifierExpression.GetOperandThroughParenthesis();
    if (qualifierExpression.IsThisOrBaseOrNull())
    {
      var typeDeclaration = referenceExpression.GetContainingTypeDeclaration();
      if (typeDeclaration is { DeclaredElement: IStruct structTypeElement })
      {
        return TypeFactory.CreateType(structTypeElement);
      }

      return null;
    }

    var expressionType = qualifierExpression.GetExpressionType();
    return expressionType.ToIType();
  }

  [Pure]
  private static bool IsStructVirtualMethodInvocationOptimizedAtRuntime(
    IInvocationExpression invocationExpression, IMethod method, IType qualifierType, ElementProblemAnalyzerData data)
  {
    switch (method.ShortName)
    {
      case nameof(GetHashCode):
        return IsOptimizedEnumGetHashCode(qualifierType, data);

      case nameof(Enum.HasFlag):
        return IsOptimizedEnumHasFlagsInvocation(invocationExpression, qualifierType, data);
    }

    return false;
  }

  [Pure]
  private static bool IsOptimizedEnumGetHashCode(IType qualifierType, ElementProblemAnalyzerData data)
  {
    // .NET Core optimizes 'someEnum.GetHashCode()' at runtime, even in Debug mode
    return (qualifierType.IsEnumType() || qualifierType.IsTypeParameterTypeWithEnumConstraint())
           && data.GetTargetRuntime() == TargetRuntime.NetCore;
  }

  [Pure]
  public static bool IsOptimizedEnumHasFlagsInvocation(
    IInvocationExpression invocationExpression, IType qualifierType, ElementProblemAnalyzerData data)
  {
    // .NET Core optimizes whole 'someEnum.HasFlag(someOtherEnum)' at runtime in Release builds

    var singleArgument = invocationExpression.ArgumentsEnumerable.SingleItem;
    if (singleArgument == null) return false;

    var argumentValue = singleArgument.Value;
    if (argumentValue == null) return false;

    var argumentType = argumentValue.GetExpressionType().ToIType();
    if (argumentType == null) return false;

    // `nullableEnum?.HasFlag()` is also optimized
    if (invocationExpression.InvokedExpression.GetOperandThroughParenthesis()
          is IConditionalAccessExpression { HasConditionalAccessSign: true })
    {
      qualifierType = qualifierType.Unlift();
    }

    if (!TypeEqualityComparer.Default.Equals(argumentType, qualifierType)) return false;

    return data.GetTargetRuntime() == TargetRuntime.NetCore
           && data.AnalyzeCodeLikeIfOptimizationsAreEnabled();
  }
}