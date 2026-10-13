using System;
class CompoundInterest {
    static void Main() {
        double principal = 1000.0;
        double rate = 0.05;
        int years = 10;
        double amount = principal;
        int i = 0;
        while (i < years) {
            amount = amount * (1 + rate);
            i++;
        }
        Console.WriteLine(amount);
    }
}