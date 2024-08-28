using System.Reflection;
using System.Reflection.Metadata;
using System.Security.Cryptography.X509Certificates;
using AsyncHandler.EventSourcing.Events;
using Microsoft.Data.SqlClient;

namespace AsyncHandler.EventSourcing.Extensions;

public static class TypeExtensions
{
    public static MethodInfo GetApply(this Type type, SourceEvent e) =>
        type.GetMethods().FirstOrDefault(m => m.Equals("Apply") &&
        m.Attributes == MethodAttributes.Private &&
        m.GetParameters().First().ParameterType == e.GetType())
        ?? throw new Exception($"No handler defined for the {e.GetType()} event.");

    
    public static void InvokeApply(this Type type, SourceEvent e)
    {
        var apply = type.GetApply(e);
        try
        {
            apply.Invoke(type, [e]);
        }
        catch(TargetInvocationException){ throw; }
    }
    public static T CreateAggregate<T>(this Type type, string aggregateId)
    {
        var constructor = type.GetConstructor([typeof(AggregateRoot)]);
        try
        {
            var aggregate = constructor?.Invoke(type, [aggregateId]) ??
                throw new Exception($"Provided type {typeof(T)} is not an aggregate.");
            return (T) aggregate;
        }
        catch(TargetInvocationException) { throw; }
        catch(Exception) { throw; }
    }
    public static Type? GetClientAggregate(this Type type, Assembly caller)
    {
        var aggregate = caller.GetTypes()
        .FirstOrDefault(x => typeof(AggregateRoot).IsAssignableFrom(x));
        if(aggregate != null)
            return aggregate;

        var mustReferenceAssembly = typeof(AggregateRoot).Assembly.GetName();

        var ideals = caller.GetReferencedAssemblies().Where(x => 
        Assembly.Load(x).GetReferencedAssemblies()
        .Any(x => AssemblyName.ReferenceMatchesDefinition(x, mustReferenceAssembly)));

        foreach (var assemblyName in ideals)
        {
            aggregate = Assembly.Load(assemblyName).GetTypes()
            .FirstOrDefault(t => typeof(AggregateRoot).IsAssignableFrom(t));
            if(aggregate != null)
                return aggregate;
        }
        return aggregate;
    }
}