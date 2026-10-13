using System;
class Payroll {
    static void Main() {
        string employee = "JOHN DOE";
        double hours = 40.0;
        double rate = 25.5;
        double gross = hours * rate;
        double tax = gross * 0.20;
        double net = gross - tax;
        Console.WriteLine(employee);
        Console.WriteLine("Gross:");
        Console.WriteLine(gross);
        Console.WriteLine("Net:");
        Console.WriteLine(net);
    }
}