using System.Reflection;
using Bijecta.CliAuth.Core;

namespace Bijecta.CliAuth.UnitTests;

public sealed class PublicApiBoundaryTests
{
    [Fact]
    public void PublicContractsDoNotExposeProtocolDependencies()
    {
        var exposed = new HashSet<Type>();
        foreach (var type in typeof(OidcProfile).Assembly.GetExportedTypes())
        {
            exposed.Add(type);
            foreach (var contract in type.GetInterfaces())
            {
                Add(contract, exposed);
            }
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance |
                         BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                switch (member)
                {
                    case MethodBase method:
                        foreach (var parameter in method.GetParameters())
                        {
                            Add(parameter.ParameterType, exposed);
                        }
                        if (method is MethodInfo info)
                        {
                            Add(info.ReturnType, exposed);
                        }
                        break;
                    case PropertyInfo property:
                        Add(property.PropertyType, exposed);
                        break;
                    case FieldInfo field:
                        Add(field.FieldType, exposed);
                        break;
                }
            }
        }
        Assert.DoesNotContain(exposed, type =>
            (type.Namespace ?? "").StartsWith("Duende.", StringComparison.Ordinal) ||
            (type.Namespace ?? "").StartsWith("Microsoft.IdentityModel", StringComparison.Ordinal));
    }

    private static void Add(Type type, HashSet<Type> exposed)
    {
        if (!exposed.Add(type))
        {
            return;
        }
        if (type.HasElementType)
        {
            Add(type.GetElementType()!, exposed);
        }
        foreach (var argument in type.GetGenericArguments())
        {
            Add(argument, exposed);
        }
    }
}
