using System;
class GradeCalculator {
    static void Main() {
        int score = 87;
        if (score >= 90)      Console.WriteLine("A");
        else if (score >= 80) Console.WriteLine("B");
        else if (score >= 70) Console.WriteLine("C");
        else                  Console.WriteLine("F");
    }
}