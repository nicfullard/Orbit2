using System.Linq.Expressions;
using Orbit.Data.Entities;

namespace Orbit.Application;

/// <summary>
/// Sorting by an enum in SQL (spec §13 decision 47). Every enum is stored as its name, so ordering a query by the column itself
/// sorts alphabetically - a task's priority descending came out Medium, Low, High, Critical. These keys rank the value by its
/// position in the enum's declaration instead (a CASE in SQL), which is the order an in-memory sort already uses. Use them in
/// place of the bare property wherever a database query orders by an enum.
/// </summary>
public static class EnumOrder
{
    /// <summary>Low 0, Medium 1, High 2, Critical 3.</summary>
    public static readonly Expression<Func<TaskItem, int>> ByTaskPriority = Rank((TaskItem t) => t.Priority);

    /// <summary>Todo 0, InProgress 1, Waiting 2, Blocked 3, Done 4, Cancelled 5.</summary>
    public static readonly Expression<Func<TaskItem, int>> ByTaskStatus = Rank((TaskItem t) => t.Status);

    /// <summary>Active 0, OnHold 1, Completed 2, Archived 3.</summary>
    public static readonly Expression<Func<Project, int>> ByProjectStatus = Rank((Project p) => p.Status);

    /// <summary>
    /// A sort key for an enum property: its value's position in the declaration (<c>x.P == A ? 0 : x.P == B ? 1 : ... : n</c>).
    /// A value added to the enum later is ranked in its place with no change here.
    /// </summary>
    public static Expression<Func<T, int>> Rank<T, TEnum>(Expression<Func<T, TEnum>> property) where TEnum : struct, Enum
    {
        var values = Enum.GetValues<TEnum>();
        Expression key = Expression.Constant(values.Length);
        for (var i = values.Length - 1; i >= 0; i--)
            key = Expression.Condition(Expression.Equal(property.Body, Expression.Constant(values[i])), Expression.Constant(i), key);
        return Expression.Lambda<Func<T, int>>(key, property.Parameters);
    }
}
