using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using GrotheImages;

namespace GrotheImagesPlayground;

public partial class PerspectiveEditor : UserControl
{
    private readonly Point[] _points = new Point[4];
    private Ellipse _dragged;
    private Point _dragOffset;

    public PerspectiveEditor()
    {
        InitializeComponent();
        Reset(100, 100);
    }

    public double CoordinateWidth { get; set; } = 100;
    public double CoordinateHeight { get; set; } = 100;

    public void Reset(double width, double height)
    {
        CoordinateWidth = Math.Max(1, width);
        CoordinateHeight = Math.Max(1, height);
        _points[0] = new Point(0, 0);
        _points[1] = new Point(CoordinateWidth, 0);
        _points[2] = new Point(CoordinateWidth, CoordinateHeight);
        _points[3] = new Point(0, CoordinateHeight);
        LayoutPoints();
    }

    public TransformMatrix CreateMatrix(double sourceWidth, double sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) throw new ArgumentOutOfRangeException();
        double[,] a = new double[8, 8];
        double[] b = new double[8];
        Point[] source = { new Point(0, 0), new Point(sourceWidth, 0), new Point(sourceWidth, sourceHeight), new Point(0, sourceHeight) };
        for (int i = 0; i < 4; i++)
        {
            double x = source[i].X;
            double y = source[i].Y;
            double u = _points[i].X;
            double v = _points[i].Y;
            int r = i * 2;
            a[r, 0] = x; a[r, 1] = y; a[r, 2] = 1; a[r, 6] = -u * x; a[r, 7] = -u * y; b[r] = u;
            a[r + 1, 3] = x; a[r + 1, 4] = y; a[r + 1, 5] = 1; a[r + 1, 6] = -v * x; a[r + 1, 7] = -v * y; b[r + 1] = v;
        }
        double[] h = Solve(a, b);
        return new TransformMatrix(h[0], h[1], h[2], h[3], h[4], h[5], h[6], h[7], 1);
    }

    public Point[] GetPoints() => (Point[])_points.Clone();

    private void PointMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragged = (Ellipse)sender;
        Point mouse = e.GetPosition(Surface);
        _dragOffset = new Point(mouse.X - Canvas.GetLeft(_dragged), mouse.Y - Canvas.GetTop(_dragged));
        _dragged.CaptureMouse();
    }

    private void PointMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragged == null || e.LeftButton != MouseButtonState.Pressed) return;
        Point mouse = e.GetPosition(Surface);
        double x = Math.Max(0, Math.Min(Surface.Width - _dragged.Width, mouse.X - _dragOffset.X));
        double y = Math.Max(0, Math.Min(Surface.Height - _dragged.Height, mouse.Y - _dragOffset.Y));
        int index = Array.IndexOf(new[] { Point0, Point1, Point2, Point3 }, _dragged);
        _points[index] = new Point(x / Surface.Width * CoordinateWidth, y / Surface.Height * CoordinateHeight);
        LayoutPoints();
    }

    private void PointMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragged?.ReleaseMouseCapture();
        _dragged = null;
    }

    private void LayoutPoints()
    {
        Ellipse[] controls = { Point0, Point1, Point2, Point3 };
        for (int i = 0; i < controls.Length; i++)
        {
            Canvas.SetLeft(controls[i], _points[i].X / CoordinateWidth * Surface.Width - controls[i].Width / 2);
            Canvas.SetTop(controls[i], _points[i].Y / CoordinateHeight * Surface.Height - controls[i].Height / 2);
        }
        PointCollection collection = new PointCollection();
        foreach (Point p in _points) collection.Add(new Point(p.X / CoordinateWidth * Surface.Width, p.Y / CoordinateHeight * Surface.Height));
        Polygon.Points = collection;
        SetLine(TopLine, collection[0], collection[1]); SetLine(RightLine, collection[1], collection[2]);
        SetLine(BottomLine, collection[2], collection[3]); SetLine(LeftLine, collection[3], collection[0]);
    }

    private static void SetLine(Line line, Point a, Point b) { line.X1 = a.X; line.Y1 = a.Y; line.X2 = b.X; line.Y2 = b.Y; }

    private static double[] Solve(double[,] matrix, double[] vector)
    {
        int n = vector.Length;
        for (int i = 0; i < n; i++)
        {
            int pivot = i;
            for (int r = i + 1; r < n; r++) if (Math.Abs(matrix[r, i]) > Math.Abs(matrix[pivot, i])) pivot = r;
            if (Math.Abs(matrix[pivot, i]) < 1e-12) throw new InvalidOperationException("The four target points do not define a valid perspective transform.");
            for (int c = i; c < n; c++) { double t = matrix[i, c]; matrix[i, c] = matrix[pivot, c]; matrix[pivot, c] = t; }
            double value = vector[i]; vector[i] = vector[pivot]; vector[pivot] = value;
            double divisor = matrix[i, i];
            for (int c = i; c < n; c++) matrix[i, c] /= divisor;
            vector[i] /= divisor;
            for (int r = 0; r < n; r++) if (r != i)
            {
                double factor = matrix[r, i];
                for (int c = i; c < n; c++) matrix[r, c] -= factor * matrix[i, c];
                vector[r] -= factor * vector[i];
            }
        }
        return vector;
    }
}
