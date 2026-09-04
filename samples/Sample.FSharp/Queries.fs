namespace Sample.FSharp

open System.Linq
open Microsoft.FSharp.Linq.NullableOperators
open Scry
open Scry.Generated

// begin-snippet: fsharpProjectionType
/// A shape the client declares for itself, as the Blazor sample's EmployeeRow is. The constructor's
/// parameters name the projection's members, so the response comes back keyed by these names.
type EmployeeRow =
    { Name: string
      Status: Status
      Manager: string
      Department: string }
// end-snippet

module Queries =

    // begin-snippet: fsharpQuery
    /// A filter, an ordering, and a projection that reaches through two navigations. Each lambda is
    /// converted to an expression tree at the call site, as a C# lambda is, so the request that
    /// leaves is the one the C# spelling sends.
    let activeEmployees (query: ScryQuery) =
        query.Employee
            .Where(_.Active)
            .OrderBy(_.Name)
            .Select(fun e ->
                { Name = e.Name
                  Status = e.Status
                  Manager = e.Manager.Name
                  Department = e.Department.Name })
    // end-snippet

    // begin-snippet: fsharpAnonymousRecord
    /// An anonymous record declares nothing. Its fields may be written in any order — the compiler
    /// sorts them by name, and the query is the same either way.
    let headcount (query: ScryQuery) =
        query.Employee
            .GroupBy(_.Department.Name)
            .Select(fun g -> {| Headcount = g.Count(); Department = g.Key |})
    // end-snippet

    // begin-snippet: fsharpClosure
    /// Parameterized by values captured from the enclosing scope, which are evaluated here and sent
    /// as constants. A nullable member is compared with the nullable operators, which lift the
    /// constant the way C# lifts it.
    let reportsTo (query: ScryQuery) (managerId: int) (top: int) =
        query.Employee
            .Where(fun e -> e.ManagerId ?= managerId)
            .OrderBy(_.Name)
            .Take(top)
            .Select(fun e -> {| Name = e.Name; Status = e.Status |})
    // end-snippet

    // begin-snippet: fsharpLet
    /// A let inside the lambda is inlined: wherever the binding is read, the query reads what was
    /// bound to it, so the row is read twice here and nothing is computed on the client.
    let namedLike (query: ScryQuery) (fragment: string) =
        query.Employee
            .Where(fun e ->
                let name = e.Name.ToLower()
                name.Contains fragment && name.Length > 2)
            .OrderBy(_.Name)
            .Select(fun e -> {| Id = e.Id; Name = e.Name |})
    // end-snippet

    // begin-snippet: fsharpTerminals
    /// The terminals are the client's own, so a query runs inside a task like any other awaitable.
    let activeEmployeesAsync (query: ScryQuery) =
        task {
            let! rows = (activeEmployees query).ToListAsync()
            return rows
        }

    /// A terminal that takes a predicate of its own translates it the same way.
    let activeCountAsync (query: ScryQuery) =
        query.Employee.CountAsync(_.Active)
    // end-snippet
