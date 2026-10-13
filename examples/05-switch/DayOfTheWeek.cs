using System;
class DayOfTheWeek {
    static void Main() {
        int day = 3;
        switch (day) {
            case 1: Console.WriteLine("Monday"); break;
            case 2: Console.WriteLine("Tuesday"); break;
            case 3: Console.WriteLine("Wednesday"); break;
            case 4: Console.WriteLine("Thursday"); break;
            case 5: Console.WriteLine("Friday"); break;
            default: Console.WriteLine("Weekend"); break;
        }
    }
}