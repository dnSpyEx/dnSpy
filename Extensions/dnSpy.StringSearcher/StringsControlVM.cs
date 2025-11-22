using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Data;
using System.Windows.Input;
using dnlib.DotNet;
using dnSpy.Contracts.MVVM;

namespace dnSpy.StringSearcher {
	public class StringsControlVM : ViewModelBase, IGridViewColumnDescsProvider {
		private readonly StringReferencesService stringReferencesService;
		private StringReference? selectedStringLiteral;
		private string filterText = string.Empty;

		public StringsControlVM(StringReferencesService stringReferencesService) {
			this.stringReferencesService = stringReferencesService;
			StringLiteralsView = new ListCollectionView(StringLiterals);

			RefreshCommand = new RelayCommand(OnRefreshCommand);

			Descs = new GridViewColumnDescs {
				Columns = [
					new GridViewColumnDesc(StringsWindowColumnIds.Literal, Properties.dnSpy_StringSearcher_Resources.ColumnLiteral),
					new GridViewColumnDesc(StringsWindowColumnIds.Referrer, Properties.dnSpy_StringSearcher_Resources.ColumnReferrer),
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
				StringsWindowColumnIds.Referrer => new ReferrerComparer(direction),
				StringsWindowColumnIds.Literal => new LiteralComparer(direction),
				_ => throw new ArgumentOutOfRangeException(nameof(Descs))
			};
		}

		private void OnRefreshCommand(object? obj) {
			stringReferencesService.Refresh();
		}

		private class StringReferenceComparer(GridViewSortDirection Direction) : Comparer<StringReference>, IComparer<MethodBodyStringReference> {
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

			public int Compare(MethodBodyStringReference? x, MethodBodyStringReference? y) {
				if (x is null && y is null)
					return 0;
				if (x is null)
					return -1;
				if (y is null)
					return 1;

				return CompareInternal(x, y);
			}

			private int CompareInternal(StringReference x, StringReference y) {
				// Order by reference kind first.
				if (x.Kind != y.Kind) {
					return Direction switch {
						GridViewSortDirection.Default or GridViewSortDirection.Ascending => x.Kind.CompareTo(y.Kind),
						GridViewSortDirection.Descending => y.Kind.CompareTo(x.Kind),
						_ => throw new ArgumentOutOfRangeException(nameof(Direction)),
					};
				}

				// This should always be true given the closed inheritance / discriminated union of string reference types and previous Kind check.
				Debug.Assert(x.GetType() == y.GetType());

				// Enumerate all possible specialized comparisons.
				return (x, y) switch {
					(MethodBodyStringReference s1, MethodBodyStringReference s2) => CompareInternal(s1, s2),
					_ => CompareReferrerToken(x, y),
				};
			}

			protected virtual int CompareInternal(MethodBodyStringReference x, MethodBodyStringReference y) {
				int result = CompareReferrerToken(x, y);
				if (result != 0) {
					return result;
				}

				// Take into account offset to ensure stable sorting.
				return Direction switch {
					GridViewSortDirection.Default or GridViewSortDirection.Ascending => x.Offset.CompareTo(y.Offset),
					GridViewSortDirection.Descending => y.Offset.CompareTo(x.Offset),
					_ => throw new ArgumentOutOfRangeException(nameof(Direction)),
				};
			}

			protected int CompareReferrerToken(StringReference x, StringReference y) => Direction switch {
				GridViewSortDirection.Default or GridViewSortDirection.Ascending => x.Referrer.MDToken.CompareTo(y.Referrer.MDToken),
				GridViewSortDirection.Descending => y.Referrer.MDToken.CompareTo(x.Referrer.MDToken),
				_ => throw new ArgumentOutOfRangeException(nameof(Direction)),
			};
		}

		private sealed class ModuleComparer(GridViewSortDirection Direction) : StringReferenceComparer(Direction) {
			protected override int CompareInternal(MethodBodyStringReference x, MethodBodyStringReference y) {
				int result = Direction switch {
					GridViewSortDirection.Ascending => x.Module.Name.CompareTo(y.Module.Name),
					GridViewSortDirection.Descending => y.Module.Name.CompareTo(x.Module.Name),
					GridViewSortDirection.Default => 0,
					_ => throw new ArgumentOutOfRangeException(nameof(Direction)),
				};

				return result == 0
					? base.CompareInternal(x, y)
					: result;
			}
		}

		private sealed class ReferrerComparer(GridViewSortDirection Direction) : StringReferenceComparer(Direction) {
			protected override int CompareInternal(MethodBodyStringReference x, MethodBodyStringReference y) {
				int result = Direction switch {
					GridViewSortDirection.Ascending => CompareCore(x.Referrer, y.Referrer),
					GridViewSortDirection.Descending => CompareCore(y.Referrer, x.Referrer),
					GridViewSortDirection.Default => 0,
					_ => throw new ArgumentOutOfRangeException(nameof(Direction)),
				};

				return result == 0
					? base.CompareInternal(x, y)
					: result;
			}

			private static int CompareCore(IMemberDef x, IMemberDef y) {
				int result = x.DeclaringType.Name.CompareTo(y.DeclaringType.Name);
				if (result == 0)
					result = x.Name.CompareTo(y.Name);
				return result;
			}
		}

		private sealed class LiteralComparer(GridViewSortDirection Direction) : StringReferenceComparer(Direction) {
			protected override int CompareInternal(MethodBodyStringReference x, MethodBodyStringReference y) {
				int result = Direction switch {
					GridViewSortDirection.Ascending => string.Compare(x.Literal, y.Literal, StringComparison.OrdinalIgnoreCase),
					GridViewSortDirection.Descending => string.Compare(y.Literal, x.Literal, StringComparison.OrdinalIgnoreCase),
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
