using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Data;
using System.Windows.Input;
using dnSpy.Contracts.MVVM;

namespace dnSpy.StringSearcher {
	public class StringsControlVM : ViewModelBase, IGridViewColumnDescsProvider {
		private readonly StringReferencesService stringReferencesService;

		public StringsControlVM(StringReferencesService stringReferencesService) {
			this.stringReferencesService = stringReferencesService;
			StringLiteralsView = new ListCollectionView(StringLiterals);

			RefreshCommand = new RelayCommand(OnRefreshCommand);

			Descs = new GridViewColumnDescs {
				Columns = [
					new GridViewColumnDesc(StringsWindowColumnIds.Literal, Properties.dnSpy_StringSearcher_Resources.ColumnLiteral),
					new GridViewColumnDesc(StringsWindowColumnIds.Kind, Properties.dnSpy_StringSearcher_Resources.ColumnKind),
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

		public StringReference? SelectedStringReference {
			get => field;
			set {
				if (field != value) {
					field = value;
					OnPropertyChanged();
				}
			}
		}

		public GridViewColumnDescs Descs { get; }

		public string SearchText {
			get => field;
			set {
				if (field != value) {
					field = value;
					OnPropertyChanged();
					ApplyFilter();
				}
			}
		} = string.Empty;

		public bool SearchPanelVisible {
			get => field;
			set {
				if (field != value) {
					field = value;
					OnPropertyChanged();
					ApplyFilter();
				}
			}
		}

		public bool SearchCaseSensitive {
			get => field;
			set {
				if (field != value) {
					field = value;
					OnPropertyChanged();
					ApplyFilter();
				}
			}
		}

		public bool SearchIsRegex {
			get => field;
			set {
				if (field != value) {
					field = value;
					OnPropertyChanged();
					ApplyFilter();
				}
			}
		}

		public bool SearchMatchFormattedString {
			get => field;
			set {
				if (field != value) {
					field = value;
					OnPropertyChanged();
					ApplyFilter();
				}
			}
		}

		private string CurrentSearchError {
			get => field;
			set {
				if (field != value) {
					field = value;
					HasErrorUpdated();
				}
			}
		} = string.Empty;

		public override bool HasError => !string.IsNullOrEmpty(Verify(nameof(SearchText)));

		protected override string? Verify(string columnName) {
			if (columnName == nameof(SearchText)) {
				return CurrentSearchError;
			}
			return string.Empty;
		}

		private void ApplyFilter() {
			// Note: make sure to capture state of all options in the closure for consistent filtering.

			Func<object, string> getStringToMatch = SearchMatchFormattedString
				? static x => ((StringReference)x).FormattedLiteral
				: static x => ((StringReference)x).Literal;

			if (SearchIsRegex) {
				try {
					var regex = new Regex(SearchText, SearchCaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
					CurrentSearchError = string.Empty;

					StringLiteralsView.Filter = x => regex.IsMatch(getStringToMatch(x));
				}
				catch (ArgumentException) {
					CurrentSearchError = dnSpy.StringSearcher.Properties.dnSpy_StringSearcher_Resources.SearchInvalidRegexPattern;
				}
			}
			else {
				CurrentSearchError = string.Empty;

				string filterText = SearchText;
				var comparison = SearchCaseSensitive
					? StringComparison.Ordinal
					: StringComparison.OrdinalIgnoreCase;

				StringLiteralsView.Filter = x => getStringToMatch(x).Contains(filterText, comparison);
			}
		}

		private void UpdateSortDescriptions() {
			var direction = Descs.SortedColumn.Direction;
			if (Descs.SortedColumn.Column is null || direction == GridViewSortDirection.Default) {
				StringLiteralsView.CustomSort = new StringReferenceComparer(direction);
				return;
			}

			StringLiteralsView.CustomSort = Descs.SortedColumn.Column.Id switch {
				StringsWindowColumnIds.Literal => new LiteralComparer(direction),
				StringsWindowColumnIds.Kind => new StringReferenceComparer(direction),
				StringsWindowColumnIds.Referrer => new ReferrerComparer(direction),
				StringsWindowColumnIds.Module => new ModuleComparer(direction),
				_ => throw new ArgumentOutOfRangeException(nameof(Descs))
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
				// Order by reference kind first.
				if (x.Kind != y.Kind) {
					return Direction switch {
						GridViewSortDirection.Default or GridViewSortDirection.Ascending => x.Kind.CompareTo(y.Kind),
						GridViewSortDirection.Descending => y.Kind.CompareTo(x.Kind),
						_ => throw new ArgumentOutOfRangeException(nameof(Direction)),
					};
				}

				// This should always be true given the closed inheritance / discriminated union of StringReference.
				Debug.Assert(x.GetType() == y.GetType());

				// Use specialized comparisons where possible.
				return (x, y) switch {
					(ILStringReference s1, ILStringReference s2) => CompareInternal(s1, s2),
					_ => CompareReferrerToken(x, y),
				};
			}

			protected virtual int CompareInternal(ILStringReference x, ILStringReference y) {
				// First compare by token like normal.
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
				GridViewSortDirection.Default or GridViewSortDirection.Ascending => x.Token.CompareTo(y.Token),
				GridViewSortDirection.Descending => y.Token.CompareTo(x.Token),
				_ => throw new ArgumentOutOfRangeException(nameof(Direction)),
			};
		}

		private sealed class LiteralComparer(GridViewSortDirection Direction) : StringReferenceComparer(Direction) {
			protected override int CompareInternal(StringReference x, StringReference y) {
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

		private sealed class ReferrerComparer(GridViewSortDirection Direction) : StringReferenceComparer(Direction) {
			protected override int CompareInternal(StringReference x, StringReference y) {
				int result = Direction switch {
					GridViewSortDirection.Ascending => string.Compare(x.ReferrerString, y.ReferrerString, StringComparison.OrdinalIgnoreCase),
					GridViewSortDirection.Descending => string.Compare(y.ReferrerString, x.ReferrerString, StringComparison.OrdinalIgnoreCase),
					GridViewSortDirection.Default => 0,
					_ => throw new ArgumentOutOfRangeException(nameof(Direction)),
				};

				return result == 0
					? base.CompareInternal(x, y)
					: result;
			}
		}

		private sealed class ModuleComparer(GridViewSortDirection Direction) : StringReferenceComparer(Direction) {
			protected override int CompareInternal(ILStringReference x, ILStringReference y) {
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
	}
}
