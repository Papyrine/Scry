// The properties that read as a function rather than as a path the server walks.
sealed partial class QueryTranslator
{
    // A property that reads as a function rather than a member path: a date part, or string length.
    static bool IsKnownProperty(MemberExpression member, out KnownFunction function)
    {
        var declaring = member.Member.DeclaringType;
        if (member.Expression is not null)
        {
            if (IsTemporal(declaring))
            {
                switch (member.Member.Name)
                {
                    case "Year":
                        function = KnownFunction.DateYear;
                        return true;
                    case "Month":
                        function = KnownFunction.DateMonth;
                        return true;
                    case "Day":
                        function = KnownFunction.DateDay;
                        return true;
                    case "Hour":
                        function = KnownFunction.DateHour;
                        return true;
                    case "Minute":
                        function = KnownFunction.DateMinute;
                        return true;
                    case "Second":
                        function = KnownFunction.DateSecond;
                        return true;
                    case "Millisecond":
                        function = KnownFunction.DateMillisecond;
                        return true;
                    case "DayOfYear":
                        function = KnownFunction.DateDayOfYear;
                        return true;
                    case "Microsecond":
                        function = KnownFunction.DateMicrosecond;
                        return true;
                    case "Nanosecond":
                        function = KnownFunction.DateNanosecond;
                        return true;
                    case "DayNumber":
                        function = KnownFunction.DateDayNumber;
                        return true;
                    case "TimeOfDay":
                        function = KnownFunction.DateTimeOfDay;
                        return true;
                    case "DayOfWeek":
                        function = KnownFunction.DateDayOfWeek;
                        return true;
                    case "Date":
                        function = KnownFunction.DateDate;
                        return true;
                }
            }

            // An elapsed time's parts are spelled in the plural — Hours, not Hour — which is what tells
            // them apart from a date's, and is why they are read here rather than alongside them. The
            // Total* readings are absent on purpose: each is a division rather than a part, and no
            // provider translates one.
            if (declaring == typeof(TimeSpan))
            {
                switch (member.Member.Name)
                {
                    case "Hours":
                        function = KnownFunction.TimeSpanHours;
                        return true;
                    case "Minutes":
                        function = KnownFunction.TimeSpanMinutes;
                        return true;
                    case "Seconds":
                        function = KnownFunction.TimeSpanSeconds;
                        return true;
                    case "Milliseconds":
                        function = KnownFunction.TimeSpanMilliseconds;
                        return true;
                    case "Microseconds":
                        function = KnownFunction.TimeSpanMicroseconds;
                        return true;
                    case "Nanoseconds":
                        function = KnownFunction.TimeSpanNanoseconds;
                        return true;
                }
            }

            if (declaring == typeof(string) &&
                member.Member.Name == "Length")
            {
                function = KnownFunction.StringLength;
                return true;
            }
        }

        function = default;
        return false;
    }
}
