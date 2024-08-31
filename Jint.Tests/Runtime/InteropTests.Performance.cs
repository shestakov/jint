#nullable enable
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Jint.Native;
using Jint.Runtime.Interop;

namespace Jint.Tests.Runtime;

public partial class InteropTests
{
    [Fact]
    public void CallInteropMethodWithGuidParameter()
    {
        var instance = new Injected();

        _engine.SetValue("InjectedInstance", instance);

        var sw = new Stopwatch();

        sw.Start();

        const int numberOfCalls = 1_000_000;
        for (var i = 0; i < numberOfCalls; i++)
        {
            _engine.Execute("InjectedInstance.Method('244002F3-563E-4742-8A8A-039577A20F0A');");
        }

        sw.Stop();

        _testOutputHelper.WriteLine($"{numberOfCalls} completed in {sw.Elapsed}");
    }

    private class Injected
    {
        public void Method(Guid parameter) {}
    }

    private class JintGuidConverter : IObjectConverter
    {
        public bool TryConvert(Engine engine, object value, out JsValue result)
        {
            if (value is Guid guid)
            {
                result = guid.ToString();
                return true;
            }

            result = JsValue.Null;
            return false;
        }
    }

    public class JintTypeConverter : DefaultTypeConverter
    {
        public JintTypeConverter(Engine engine) : base(engine)
        {
        }

        public override bool TryConvert(object? value, Type type, IFormatProvider formatProvider, [NotNullWhen(true)] out object? converted)
        {
            try
            {
                if (type == typeof(Guid))
                {
                    if (value != null)
                    {
                        converted = new Guid((string)value);
                        return true;
                    }
                }

                if (type == typeof(Guid?))
                {
                    if (value == null)
                    {
                        converted = null;
                    }
                    else
                    {
                        converted = new Guid((string) value);
                        return true;
                    }
                }
            }
            catch
            {
                converted = null;
                return false;
            }

            return base.TryConvert(value, type, formatProvider, out converted);
        }

        public override object? Convert(object? value, Type type, IFormatProvider formatProvider)
        {
            if (type == typeof(Guid))
            {
                if (value == null)
                {
                    throw new ArgumentNullException(nameof(value));
                }

                return new Guid((string)value);
            }

            if (type == typeof(Guid?))
            {
                return value == null ? null : new Guid((string)value);
            }

            return base.Convert(value, type, formatProvider);
        }
    }

}
