using System;
using System.Reflection;

namespace SpectateMate.Ui;

/// <summary>
/// 访问游戏私有成员的反射工具。
///
/// 关键点：Type.GetField / GetMethod 不会返回基类声明的私有成员。
/// NCombatCardPile._pile / _currentCount / _countLabel 都定义在基类上，
/// 直接对派生类型查找只会拿到 null，从而静默失效——
/// 之前「三个牌堆全被绑成弃牌堆」就是这个问题。这里一律沿继承链逐级查找。
/// </summary>
internal static class ReflectionUtil
{
    public static FieldInfo? Field(Type type, string name)
    {
        const BindingFlags flags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        for (Type? current = type; current != null && current != typeof(object); current = current.BaseType)
        {
            FieldInfo? field = current.GetField(name, flags);
            if (field != null)
            {
                return field;
            }
        }

        return null;
    }

    public static MethodInfo? Method(Type type, string name)
    {
        const BindingFlags flags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        for (Type? current = type; current != null && current != typeof(object); current = current.BaseType)
        {
            MethodInfo? method = current.GetMethod(name, flags);
            if (method != null)
            {
                return method;
            }
        }

        return null;
    }

    public static object? Peek(object instance, string fieldName)
    {
        try
        {
            return Field(instance.GetType(), fieldName)?.GetValue(instance);
        }
        catch
        {
            return null;
        }
    }

    public static T? Find<T>(object instance, string fieldName) where T : class
    {
        try
        {
            return Field(instance.GetType(), fieldName)?.GetValue(instance) as T;
        }
        catch
        {
            return null;
        }
    }

    public static T Read<T>(object instance, string fieldName)
    {
        try
        {
            object? value = Field(instance.GetType(), fieldName)?.GetValue(instance);
            return value is T typed ? typed : default!;
        }
        catch
        {
            return default!;
        }
    }

    public static void Set(object instance, string fieldName, object? value)
    {
        try
        {
            Field(instance.GetType(), fieldName)?.SetValue(instance, value);
        }
        catch
        {
            // ignored
        }
    }

    public static void Invoke(object instance, string methodName)
    {
        try
        {
            Method(instance.GetType(), methodName)?.Invoke(instance, null);
        }
        catch
        {
            // ignored
        }
    }

    public static void InvokeMethod(object instance, string methodName, params object?[] args)
    {
        try
        {
            Method(instance.GetType(), methodName)?.Invoke(instance, args);
        }
        catch
        {
            // ignored
        }
    }
}
