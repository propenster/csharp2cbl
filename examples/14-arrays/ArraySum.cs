using System;
class ArraySum {
    static void Main() {
        int[] nums = new int[5];
        int i = 0;
        while (i < 5) { nums[i] = i + 1; i++; }
        int sum = 0;
        i = 0;
        while (i < 5) { sum = sum + nums[i]; i++; }
        Console.WriteLine(sum);
    }
}