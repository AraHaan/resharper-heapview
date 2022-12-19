var func0 = new DelegateOptimization<bool>().InstanceMethod; // alloc
var func1 = DelegateOptimization<int>.StaticMethod;
var func2 = LocalFunction; // alloc
var func3 = StaticLocalFunction;
var func4 = () => { };
var func5 = static () => { };

void LocalFunction() { }
static void StaticLocalFunction() { }

public class DelegateOptimization<T> {
  public static void StaticMethod() { }
  public void InstanceMethod() { }
}