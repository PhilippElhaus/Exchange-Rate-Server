using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ExchangeRateServer;

public static class Res
{
	public static readonly BitmapImage Red = new(new Uri(@"images\status_red.png", UriKind.Relative));
	public static readonly BitmapImage Yellow = new(new Uri(@"images\status_yellow.png", UriKind.Relative));
	public static readonly BitmapImage Green = new(new Uri(@"images\status_green.png", UriKind.Relative));
	public static readonly Icon On = new(@"images\green.ico");
	public static readonly Icon Off = new(@"images\red.ico");
	public static readonly Icon Connected = new(@"images\yellow.ico");

	public static readonly List<string> FIAT = new List<string>() { "USD", "EUR", "JPY", "CAD", "GBP", "CNY", "NZD", "AUD", "CHF" };
}

public static class Ext
{
	public static string Short(this Exception ex) => ex.Message + ex.ToString().Remove(0, ex.ToString().IndexOf(":line"));

	public static void FileCheck(string filename, string directory = "")
	{
		if (directory != "")
		{
			if (!Directory.Exists(directory))
				Directory.CreateDirectory(directory);

			if (!File.Exists(directory + @"\" + filename))
				using (File.Create(directory + @"\" + filename))
				{ };
			FileInfo fileInfo = new FileInfo(directory + @"\" + filename);
			if (fileInfo.Length > 262144)
			{ fileInfo.Delete(); using (File.Create(directory + @"\" + filename)) { }; }
		}
		else
		{
			if (!File.Exists(filename))
				using (File.Create(filename))
				{ };
			FileInfo fileInfo = new FileInfo(filename);
			if (fileInfo.Length > 262144)
			{ fileInfo.Delete(); using (File.Create(filename)) { }; }
		}
	}

	public static decimal TruncateDecimal(decimal value, int precision)
	{
		decimal step = (decimal)Math.Pow(10, precision);
		decimal tmp = Math.Truncate(step * value);
		return tmp / step;
	}

	[DllImport("wininet.dll")]
	internal static extern bool InternetGetConnectedState(out int Val, int ReservedValue);
}

public class GridViewSort
{
	public static ICommand GetCommand(DependencyObject obj)
	{
		return (ICommand)obj.GetValue(CommandProperty);
	}

	public static void SetCommand(DependencyObject obj, ICommand value)
	{
		obj.SetValue(CommandProperty, value);
	}

	public static readonly DependencyProperty CommandProperty =
		DependencyProperty.RegisterAttached(
			"Command",
			typeof(ICommand),
			typeof(GridViewSort),
			new UIPropertyMetadata(
				null,
				(o, e) =>
				{
					ItemsControl listView = o as ItemsControl;
					if (listView != null)
					{
						if (!GetAutoSort(listView))
						{
							if (e.OldValue != null && e.NewValue == null)
							{
								listView.RemoveHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(ColumnHeader_Click));
							}
							if (e.OldValue == null && e.NewValue != null)
							{
								listView.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(ColumnHeader_Click));
							}
						}
					}
				}
			)
		);

	public static bool GetAutoSort(DependencyObject obj)
	{
		return (bool)obj.GetValue(AutoSortProperty);
	}

	public static void SetAutoSort(DependencyObject obj, bool value)
	{
		obj.SetValue(AutoSortProperty, value);
	}

	public static readonly DependencyProperty AutoSortProperty =
		DependencyProperty.RegisterAttached(
			"AutoSort",
			typeof(bool),
			typeof(GridViewSort),
			new UIPropertyMetadata(
				false,
				(o, e) =>
				{
					ListView listView = o as ListView;
					if (listView != null)
					{
						if (GetCommand(listView) == null)
						{
							bool oldValue = (bool)e.OldValue;
							bool newValue = (bool)e.NewValue;
							if (oldValue && !newValue)
							{
								listView.RemoveHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(ColumnHeader_Click));
							}
							if (!oldValue && newValue)
							{
								listView.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(ColumnHeader_Click));
							}
						}
					}
				}
			)
		);

	public static string GetPropertyName(DependencyObject obj)
	{
		return (string)obj.GetValue(PropertyNameProperty);
	}

	public static void SetPropertyName(DependencyObject obj, string value)
	{
		obj.SetValue(PropertyNameProperty, value);
	}

	public static readonly DependencyProperty PropertyNameProperty =
		DependencyProperty.RegisterAttached(
			"PropertyName",
			typeof(string),
			typeof(GridViewSort),
			new UIPropertyMetadata(null)
		);

	private static void ColumnHeader_Click(object sender, RoutedEventArgs e)
	{
		GridViewColumnHeader headerClicked = e.OriginalSource as GridViewColumnHeader;
		if (headerClicked != null)
		{
			string propertyName = GetPropertyName(headerClicked.Column);
			if (!string.IsNullOrEmpty(propertyName))
			{
				ListView listView = GetAncestor<ListView>(headerClicked);
				if (listView != null)
				{
					ICommand command = GetCommand(listView);
					if (command != null)
					{
						if (command.CanExecute(propertyName))
						{
							command.Execute(propertyName);
						}
					}
					else if (GetAutoSort(listView))
					{
						ApplySort(listView.Items, propertyName);
					}
				}
			}
		}
	}

	public static T GetAncestor<T>(DependencyObject reference) where T : DependencyObject
	{
		DependencyObject parent = VisualTreeHelper.GetParent(reference);
		while (!(parent is T))
		{
			parent = VisualTreeHelper.GetParent(parent);
		}
		if (parent != null)
			return (T)parent;
		else
			return null;
	}

	public static void ApplySort(ICollectionView view, string propertyName)
	{
		ListSortDirection direction = ListSortDirection.Ascending;
		if (view.SortDescriptions.Count > 0)
		{
			SortDescription currentSort = view.SortDescriptions[0];
			if (currentSort.PropertyName == propertyName)
			{
				if (currentSort.Direction == ListSortDirection.Ascending)
					direction = ListSortDirection.Descending;
				else
					direction = ListSortDirection.Ascending;
			}
			view.SortDescriptions.Clear();
		}
		if (!string.IsNullOrEmpty(propertyName))
		{
			view.SortDescriptions.Add(new SortDescription(propertyName, direction));
		}
	}
}