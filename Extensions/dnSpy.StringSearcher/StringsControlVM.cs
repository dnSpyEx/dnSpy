using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Data;
using System.Windows.Input;
using dnSpy.Contracts.MVVM;

namespace dnSpy.StringSearcher {

	public class StringsControlVM : ViewModelBase, IGridViewColumnDescsProvider {
		private StringReferencesService stringReferencesService;
		private StringReference? selectedStringLiteral;
		private string filterText = string.Empty;

		public StringsControlVM(StringReferencesService stringReferencesService) {
			this.stringReferencesService = stringReferencesService;
			StringLiteralsView = new ListCollectionView(StringLiterals);

			RefreshCommand = new RelayCommand(OnRefreshCommand);

			Descs = new GridViewColumnDescs {
				Columns = [
					new GridViewColumnDesc(StringsWindowColumnIds.Literal, Properties.dnSpy_StringSearcher_Resources.ColumnLiteral),
					new GridViewColumnDesc(StringsWindowColumnIds.Method, Properties.dnSpy_StringSearcher_Resources.ColumnMethod),
					new GridViewColumnDesc(StringsWindowColumnIds.Module, Properties.dnSpy_StringSearcher_Resources.ColumnModule),
				],
			};
			Descs.SortedColumnChanged += (_, _) => UpdateSortDescriptions();

			UpdateSortDescriptions();
		}

		public ObservableCollection<StringReference> StringLiterals { get; } = [];

		public ListCollectionView StringLiteralsView { get; }

		public ICommand RefreshCommand { get; }

		public string FilterText {
			get => filterText;
			set {
				if (filterText != value) {
					filterText = value;
					OnPropertyChanged(nameof(FilterText));
					ApplyFilter(filterText);
				}
			}
		}

		public StringReference? SelectedStringReference {
			get => selectedStringLiteral;
			set {
				if (selectedStringLiteral != value) {
					selectedStringLiteral = value;
					OnPropertyChanged(nameof(SelectedStringReference));
				}
			}
		}
		public GridViewColumnDescs Descs { get; }

		private void ApplyFilter(string filterText) {
			StringLiteralsView.Filter = x => x is StringReference reference
				&& reference.FormattedLiteral.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) != -1;
		}

		private void UpdateSortDescriptions() {
			var direction = Descs.SortedColumn.Direction;
			if (Descs.SortedColumn.Column is null || direction == GridViewSortDirection.Default) {
				StringLiteralsView.CustomSort = new StringReferenceComparer(direction);
				return;
			}

			StringLiteralsView.CustomSort = Descs.SortedColumn.Column.Id switch {
				StringsWindowColumnIds.Module => new ModuleComparer(direction),
				StringsWindowColumnIds.Method => new MethodComparer(direction),
				StringsWindowColumnIds.Literal => new LiteralComparer(direction),
				_ => throw new NotImplementedException()
			};
		}

		private void OnRefreshCommand(object? obj) {
			stringReferencesService.Refresh();
		}

		private class StringReferenceComparer(GridViewSortDirection Direction) : Comparer<StringReference> {
			public GridViewSortDirection Direction { get; } = Direction;

			public sealed override int Compare(StringReference? x, StringReference? y) {
				if (x is null && y is null)
					return 0;
				if (x is null)
					return -1;
				if (y is null)
					return 1;

				return CompareInternal(x, y);
			}

			protected virtual int CompareInternal(StringReference x, StringReference y) {
				int result = Direction switch {
					GridViewSortDirection.Default or GridViewSortDirection.Ascending => x.Referrer.MDToken.CompareTo(y.Referrer.MDToken),
					GridViewSortDirection.Descending => y.Referrer.MDToken.CompareTo(x.Referrer.MDToken),
					_ => throw new ArgumentOutOfRangeException(),
				};

				if (result != 0) {
					return result;
				}

				return Direction switch {
					GridViewSortDirection.Default or GridViewSortDirection.Ascending => x.Offset.CompareTo(y.Offset),
					GridViewSortDirection.Descending => y.Offset.CompareTo(x.Offset),
					_ => throw new ArgumentOutOfRangeException(nameof(Direction)),
				};
			}
		}

		private sealed class ModuleComparer(GridViewSortDirection Direction) : StringReferenceComparer(Direction) {
			protected override int CompareInternal(StringReference x, StringReference y) {
				int result = Direction switch {
					GridViewSortDirection.Ascending => x.Referrer.Module.Name.CompareTo(y.Referrer.Module.Name),
					GridViewSortDirection.Descending => y.Referrer.Module.Name.CompareTo(x.Referrer.Module.Name),
					GridViewSortDirection.Default => 0,
					_ => throw new ArgumentOutOfRangeException(nameof(Direction)),
				};

				return result == 0
					? base.CompareInternal(x, y)
					: result;
			}
		}

		private sealed class MethodComparer(GridViewSortDirection Direction) : StringReferenceComparer(Direction) {
			protected override int CompareInternal(StringReference x, StringReference y) {
				int result = Direction switch {
					GridViewSortDirection.Ascending => CompareCore(x, y),
					GridViewSortDirection.Descending => CompareCore(y, x),
					GridViewSortDirection.Default => 0,
					_ => throw new ArgumentOutOfRangeException(nameof(Direction)),
				};

				return result == 0
					? base.CompareInternal(x, y)
					: result;
			}

			private static int CompareCore(StringReference x, StringReference y) {
				int result = x.Referrer.DeclaringType.Name.CompareTo(y.Referrer.DeclaringType.Name);
				if (result == 0)
					result = x.Referrer.Name.CompareTo(y.Referrer.Name);
				return result;
			}
		}

		private sealed class LiteralComparer(GridViewSortDirection Direction) : StringReferenceComparer(Direction) {
			protected override int CompareInternal(StringReference x, StringReference y) {
				int result = Direction switch {
					GridViewSortDirection.Ascending => x.Literal.CompareTo(y.Literal),
					GridViewSortDirection.Descending => y.Literal.CompareTo(x.Literal),
					GridViewSortDirection.Default => 0,
					_ => throw new ArgumentOutOfRangeException(nameof(Direction)),
				};

				return result == 0
					? base.CompareInternal(x, y)
					: result;
			}
		}
	}
}
