import { Button, DataGrid, DataGridBody, DataGridCell, DataGridHeader, DataGridHeaderCell, DataGridRow, Input, Menu, MenuItemCheckbox, MenuList, MenuPopover, MenuTrigger, Popover, PopoverSurface, PopoverTrigger, createTableColumn, type TableColumnDefinition, type TableColumnSizingOptions } from "@fluentui/react-components";
import { Columns3, GripVertical, ListFilter, Search } from "lucide-react";
import { useCallback, useEffect, useMemo, useRef, useState, type DragEvent as ReactDragEvent, type KeyboardEvent as ReactKeyboardEvent } from "react";
import { DebugSnapshot, SnapshotGridSortState } from "../types";
import { compareGridValues, defaultSnapshotSort, formatPayloadValue, friendlyStageName, isSelectedSnapshot, number, parsePayload, snapshotKey } from "../dashboardUtils";
import { Text } from "./Text";
import { CopyableValue } from "./CopyableValue";

interface SnapshotGridColumn {
  id: string;
  label: string;
  value: (item: DebugSnapshot) => unknown;
  display: (item: DebugSnapshot) => string;
  copyValue?: (item: DebugSnapshot) => string;
  selectsLineage?: boolean;
}

const MIN_SNAPSHOT_COLUMN_WIDTH = 72;

const MAX_AUTO_SNAPSHOT_COLUMN_WIDTH = 360;

export function SnapshotTable({
  items,
  onSelectRow,
  selectedSnapshot,
  showStageColumn = false,
  payloadColumns: serverPayloadColumns = [],
  matchingRowCount,
  remoteColumnFilters,
  remoteSortState,
  onRemoteColumnFiltersChange,
  onRemoteSortChange,
  rowSearch,
  rowSearchStage,
  onRowSearchChange,
  hasMoreRows = false,
  loadingRows = false,
  onLoadMore,
}: {
  items: DebugSnapshot[];
  onSelectRow: (snapshot: DebugSnapshot) => void;
  selectedSnapshot: DebugSnapshot | null;
  showStageColumn?: boolean;
  payloadColumns?: string[];
  matchingRowCount?: number;
  remoteColumnFilters?: Record<string, string>;
  remoteSortState?: SnapshotGridSortState;
  onRemoteColumnFiltersChange?: (filters: Record<string, string>) => void;
  onRemoteSortChange?: (state: SnapshotGridSortState) => void;
  rowSearch?: string;
  rowSearchStage?: string;
  onRowSearchChange?: (value: string) => void;
  hasMoreRows?: boolean;
  loadingRows?: boolean;
  onLoadMore?: () => void;
}) {
  const [jsonMode, setJsonMode] = useState(false);
  const [hiddenColumnIds, setHiddenColumnIds] = useState<Set<string>>(() => new Set());
  const [columnOrder, setColumnOrder] = useState<string[]>([]);
  const [columnWidths, setColumnWidths] = useState<Record<string, number>>({});
  const [localColumnFilters, setLocalColumnFilters] = useState<Record<string, string>>({});
  const [columnMenuSearch, setColumnMenuSearch] = useState("");
  const [filterColumnSearch, setFilterColumnSearch] = useState("");
  const [filtersOpen, setFiltersOpen] = useState(false);
  const [localSortState, setLocalSortState] = useState<SnapshotGridSortState>(defaultSnapshotSort);
  const columnFilters = remoteColumnFilters ?? localColumnFilters;
  const sortState = remoteSortState ?? localSortState;
  const remoteMode = remoteColumnFilters !== undefined && remoteSortState !== undefined;
  const scrollContainerRef = useRef<HTMLDivElement | null>(null);
  const loadMoreSentinelRef = useRef<HTMLDivElement | null>(null);
  const draggedColumnId = useRef<string | null>(null);
  const changeColumnFilters = useCallback((
    update: Record<string, string> | ((current: Record<string, string>) => Record<string, string>),
  ) => {
    const next = typeof update === "function" ? update(columnFilters) : update;
    if (remoteColumnFilters === undefined) setLocalColumnFilters(next);
    else onRemoteColumnFiltersChange?.(next);
  }, [columnFilters, onRemoteColumnFiltersChange, remoteColumnFilters]);
  const changeSortState = useCallback((next: SnapshotGridSortState) => {
    if (remoteSortState === undefined) setLocalSortState(next);
    onRemoteSortChange?.(next);
  }, [onRemoteSortChange, remoteSortState]);
  useEffect(() => {
    const root = scrollContainerRef.current;
    const sentinel = loadMoreSentinelRef.current;
    if (!root || !sentinel || !onLoadMore || !hasMoreRows || loadingRows) return;
    const observer = new IntersectionObserver((entries) => {
      if (entries.some((entry) => entry.isIntersecting)) onLoadMore();
    }, { root, rootMargin: "120px" });
    observer.observe(sentinel);
    return () => observer.disconnect();
  }, [hasMoreRows, items.length, loadingRows, onLoadMore]);

  const payloads = useMemo(() => items.map((item) => parsePayload(item.payloadJson)), [items]);
  const payloadByItem = useMemo(
    () => new Map(items.map((item, index) => [item, payloads[index]] as const)),
    [items, payloads],
  );
  const payloadColumns = useMemo(
    () => [...new Set([...serverPayloadColumns, ...payloads.flatMap((payload) => Object.keys(payload))])],
    [payloads, serverPayloadColumns],
  );
  const columnConfigs = useMemo<SnapshotGridColumn[]>(() => {
    const columns: SnapshotGridColumn[] = [
      { id: "record", label: "Record", value: (item) => item.recordIndex, display: (item) => String(item.recordIndex) },
      { id: "attempt", label: "Attempt", value: (item) => item.attempt, display: (item) => String(item.attempt) },
      {
        id: "captured",
        label: "Captured",
        value: (item) => Date.parse(item.capturedUtc),
        display: (item) => new Date(item.capturedUtc).toLocaleString(),
      },
      { id: "batch", label: "Batch", value: (item) => item.batchId, display: (item) => item.batchId },
      {
        id: "lineage",
        label: "Row lineage",
        value: (item) => item.lineageId,
        display: (item) => item.lineageId,
        selectsLineage: true,
      },
    ];
    if (showStageColumn) {
      columns.push({
        id: "stage",
        label: "Stage",
        value: (item) => friendlyStageName(item.stageName),
        display: (item) => friendlyStageName(item.stageName),
      });
    }
    if (jsonMode) {
      columns.push({
        id: "payload-json",
        label: "Payload JSON",
        value: (item) => item.payloadJson,
        display: (item) => item.payloadJson.replace(/\s+/g, " "),
        copyValue: (item) => item.payloadJson,
      });
    } else {
      for (const property of payloadColumns) {
        columns.push({
          id: `payload:${property}`,
          label: property,
          value: (item) => payloadByItem.get(item)?.[property],
          display: (item) => formatPayloadValue(payloadByItem.get(item)?.[property]),
          copyValue: (item) => formatPayloadValue(payloadByItem.get(item)?.[property]),
        });
      }
    }
    return columns;
  }, [jsonMode, payloadByItem, payloadColumns, showStageColumn]);
  const orderedColumnConfigs = useMemo(() => {
    const orderIndices = new Map(columnOrder.map((columnId, index) => [columnId, index]));
    return [...columnConfigs].sort(
      (left, right) =>
        (orderIndices.get(left.id) ?? Number.MAX_SAFE_INTEGER) -
        (orderIndices.get(right.id) ?? Number.MAX_SAFE_INTEGER),
    );
  }, [columnConfigs, columnOrder]);
  const visibleColumnConfigs = useMemo(
    () => orderedColumnConfigs.filter((column) => !hiddenColumnIds.has(column.id)),
    [hiddenColumnIds, orderedColumnConfigs],
  );
  const visibleColumnIds = useMemo(
    () => visibleColumnConfigs.map((column) => column.id),
    [visibleColumnConfigs],
  );
  const searchableColumns = useMemo(() => {
    const query = columnMenuSearch.trim().toLocaleLowerCase();
    return orderedColumnConfigs.filter((column) =>
      !query ||
      column.label.toLocaleLowerCase().includes(query) ||
      column.id.toLocaleLowerCase().includes(query),
    );
  }, [columnMenuSearch, orderedColumnConfigs]);
  const filterColumnConfigs = useMemo(() => [
    ...columnConfigs.map(({ id, label }) => ({ id, label })),
    ...(jsonMode
      ? payloadColumns.map((property) => ({ id: `payload:${property}`, label: property }))
      : []),
  ], [columnConfigs, jsonMode, payloadColumns]);
  const filterableColumns = useMemo(() => {
    const query = filterColumnSearch.trim().toLocaleLowerCase();
    return filterColumnConfigs.filter((column) =>
      !query ||
      column.label.toLocaleLowerCase().includes(query) ||
      column.id.toLocaleLowerCase().includes(query),
    );
  }, [filterColumnConfigs, filterColumnSearch]);
  const activeFilterCount = Object.values(columnFilters).filter((value) => value.trim()).length;
  const filteredItems = useMemo(() => {
    if (remoteMode) return items;
    const activeFilters = columnConfigs.filter((column) => columnFilters[column.id]?.trim());
    if (!activeFilters.length) return items;
    return items.filter((item) =>
      activeFilters.every((column) =>
        column.display(item).toLocaleLowerCase().includes(columnFilters[column.id].trim().toLocaleLowerCase()),
      ),
    );
  }, [columnConfigs, columnFilters, items, remoteMode]);
  const measuredWidths = useMemo(() => {
    const canvas = document.createElement("canvas");
    const context = canvas.getContext("2d");
    if (context) context.font = "12px Segoe UI, sans-serif";
    const widths: Record<string, number> = {};
    for (const column of visibleColumnConfigs) {
      let contentWidth = context?.measureText(column.label).width ?? column.label.length * 7;
      for (const item of items) {
        const valueWidth = context?.measureText(column.display(item)).width ?? column.display(item).length * 7;
        contentWidth = Math.max(contentWidth, valueWidth);
      }
      widths[column.id] = Math.min(
        MAX_AUTO_SNAPSHOT_COLUMN_WIDTH,
        Math.max(MIN_SNAPSHOT_COLUMN_WIDTH, Math.ceil(contentWidth + 40)),
      );
    }
    return widths;
  }, [items, visibleColumnConfigs]);
  const columnSizingOptions = useMemo<TableColumnSizingOptions>(() => {
    const options: TableColumnSizingOptions = {};
    for (const column of visibleColumnConfigs) {
      const width = columnWidths[column.id] ?? measuredWidths[column.id] ?? MIN_SNAPSHOT_COLUMN_WIDTH;
      options[column.id] = {
        defaultWidth: width,
        idealWidth: width,
        minWidth: MIN_SNAPSHOT_COLUMN_WIDTH,
        padding: 24,
      };
    }
    return options;
  }, [columnWidths, measuredWidths, visibleColumnConfigs]);
  const columns = useMemo<TableColumnDefinition<DebugSnapshot>[]>(() =>
    visibleColumnConfigs.map((column) =>
      createTableColumn<DebugSnapshot>({
        columnId: column.id,
        compare: (left, right) => compareGridValues(column.value(left), column.value(right)),
        renderHeaderCell: () => (
          <span className="grid-header-label" title="Drag to reorder; press Alt+Left or Alt+Right to move">
            <GripVertical size={13} aria-hidden="true" />
            <span>{column.label}</span>
          </span>
        ),
        renderCell: (item) => {
          const displayValue = column.display(item);
          if (column.selectsLineage) {
            return (
              <button
                className="text-button lineage-link"
                type="button"
                aria-label={`Show lineage for ${item.lineageId}`}
                onClick={() => onSelectRow(item)}
              >{displayValue}</button>
            );
          }
          if (column.copyValue) {
            return (
              <CopyableValue
                label={column.label}
                displayValue={displayValue}
                copyValue={column.copyValue(item)}
              />
            );
          }
          return <span className="grid-cell-text" title={displayValue}>{displayValue}</span>;
        },
      }),
    ),
    [onSelectRow, visibleColumnConfigs],
  );
  useEffect(() => {
    if (visibleColumnConfigs.length > 0 &&
      !visibleColumnConfigs.some((column) => column.id === sortState.sortColumn)) {
      changeSortState({ sortColumn: visibleColumnConfigs[0].id, sortDirection: "ascending" });
    }
  }, [changeSortState, sortState.sortColumn, visibleColumnConfigs]);

  const moveColumn = useCallback((sourceId: string, targetId: string) => {
    if (sourceId === targetId) return;
    const nextOrder = orderedColumnConfigs.map((column) => column.id);
    const sourceIndex = nextOrder.indexOf(sourceId);
    const targetIndex = nextOrder.indexOf(targetId);
    if (sourceIndex < 0 || targetIndex < 0) return;
    nextOrder.splice(sourceIndex, 1);
    nextOrder.splice(targetIndex, 0, sourceId);
    setColumnOrder(nextOrder);
  }, [orderedColumnConfigs]);
  const moveColumnByOffset = useCallback((columnId: string, offset: number) => {
    const visibleIds = visibleColumnConfigs.map((column) => column.id);
    const currentIndex = visibleIds.indexOf(columnId);
    const targetIndex = Math.max(0, Math.min(visibleIds.length - 1, currentIndex + offset));
    if (currentIndex >= 0 && targetIndex !== currentIndex) {
      moveColumn(columnId, visibleIds[targetIndex]);
    }
  }, [moveColumn, visibleColumnConfigs]);

  return (
    <div className="snapshot-table-container">
      <div className="snapshot-table-toolbar">
        <span>{number.format(items.length)} of {number.format(matchingRowCount ?? items.length)} matching row snapshots</span>
        <div className="snapshot-grid-actions">
          {rowSearch !== undefined && onRowSearchChange && (
            <input
              className="search-input snapshot-search-input"
              type="search"
              aria-label={`Search ${friendlyStageName(rowSearchStage ?? items[0]?.stageName ?? "")} rows`}
              placeholder={`Search ${friendlyStageName(rowSearchStage ?? items[0]?.stageName ?? "")} rows`}
              value={rowSearch}
              onChange={(event) => onRowSearchChange(event.currentTarget.value)}
            />
          )}
          <Popover open={filtersOpen} onOpenChange={(_, data) => setFiltersOpen(data.open)}>
            <PopoverTrigger disableButtonEnhancement>
              <Button
                appearance="secondary"
                size="small"
                icon={<ListFilter size={14} />}
                aria-label={remoteMode ? "Filter all captured rows by column" : "Filter loaded rows by column"}
              >Filters{activeFilterCount > 0 ? ` (${activeFilterCount})` : ""}</Button>
            </PopoverTrigger>
            <PopoverSurface className="grid-filter-popover">
              <div className="grid-filter-heading">
                <Text variant="mediumPlus">{remoteMode ? "Filter all captured rows" : "Filter loaded rows"}</Text>
                <Text variant="small">
                  {remoteMode
                    ? "Each column filter is applied across all matching snapshots on the server."
                    : "Column filters apply to the loaded lineage rows."}
                </Text>
              </div>
              <Input
                size="small"
                type="search"
                contentBefore={<Search size={14} />}
                aria-label="Search filter columns"
                placeholder="Search columns"
                value={filterColumnSearch}
                onChange={(_, data) => setFilterColumnSearch(data.value)}
              />
              <div className="grid-filter-list">
                {filterableColumns.map((column) => (
                  <label className="grid-filter-field" key={column.id}>
                    <span>{column.label}</span>
                    <Input
                      size="small"
                      type="search"
                      aria-label={`Filter ${column.label}`}
                      placeholder={`Filter ${column.label}`}
                      value={columnFilters[column.id] ?? ""}
                      onChange={(_, data) => changeColumnFilters((current) => ({
                        ...current,
                        [column.id]: data.value,
                      }))}
                    />
                  </label>
                ))}
                {filterableColumns.length === 0 && <span className="inline-empty">No columns match that search.</span>}
              </div>
              <div className="grid-filter-footer">
                <Text variant="small">{activeFilterCount} active filters</Text>
                <Button
                  appearance="subtle"
                  size="small"
                  onClick={() => changeColumnFilters({})}
                  disabled={activeFilterCount === 0}
                >Clear filters</Button>
              </div>
            </PopoverSurface>
          </Popover>
          <Menu
            checkedValues={{ columns: visibleColumnIds }}
            onCheckedValueChange={(_, data) => {
              if (data.name !== "columns") return;
              const menuColumnIds = new Set(searchableColumns.map((column) => column.id));
              setHiddenColumnIds((current) => {
                const next = new Set(current);
                for (const columnId of menuColumnIds) {
                  if (data.checkedItems.includes(columnId)) next.delete(columnId);
                  else next.add(columnId);
                }
                const visibleCount = columnConfigs.filter((column) => !next.has(column.id)).length;
                return visibleCount === 0 ? current : next;
              });
            }}
          >
            <MenuTrigger disableButtonEnhancement>
              <Button
                appearance="secondary"
                size="small"
                icon={<Columns3 size={14} />}
                aria-label="Choose visible columns"
              >Columns</Button>
            </MenuTrigger>
            <MenuPopover>
              <div className="grid-column-menu">
                <Input
                  size="small"
                  type="search"
                  contentBefore={<Search size={14} />}
                  aria-label="Search columns"
                  placeholder="Search columns"
                  value={columnMenuSearch}
                  onChange={(_, data) => setColumnMenuSearch(data.value)}
                />
                <MenuList>
                  {searchableColumns.map((column) => (
                    <MenuItemCheckbox
                      key={column.id}
                      name="columns"
                      value={column.id}
                      persistOnClick
                      disabled={visibleColumnIds.length === 1 && visibleColumnIds[0] === column.id}
                    >{column.label}</MenuItemCheckbox>
                  ))}
                  {searchableColumns.length === 0 && <Text className="inline-empty">No columns match that search.</Text>}
                </MenuList>
                <Button
                  appearance="subtle"
                  size="small"
                  onClick={() => setHiddenColumnIds(new Set())}
                >Show all columns</Button>
              </div>
            </MenuPopover>
          </Menu>
          <Button
            appearance="subtle"
            size="small"
            aria-pressed={jsonMode}
            onClick={() => setJsonMode((current) => !current)}
          >{jsonMode ? "Show payload columns" : "Show payload as JSON"}</Button>
        </div>
      </div>
      <div className="snapshot-grid-scroll" ref={scrollContainerRef} role="region" aria-label="Captured row snapshots">
        <DataGrid
          className="snapshot-data-grid"
          items={filteredItems}
          columns={columns}
          getRowId={snapshotKey}
          sortable
          sortState={sortState}
          onSortChange={(_, nextSortState) => changeSortState(nextSortState)}
          resizableColumns
          resizableColumnsOptions={{ autoFitColumns: false }}
          columnSizingOptions={columnSizingOptions}
          onColumnResize={(_, data) => setColumnWidths((current) => ({
            ...current,
            [String(data.columnId)]: Math.min(data.width, MAX_AUTO_SNAPSHOT_COLUMN_WIDTH),
          }))}
        >
          <DataGridHeader>
            <DataGridRow>
              {(column) => (
                <DataGridHeaderCell
                  key={column.columnId}
                  className="snapshot-grid-header-cell"
                  draggable
                  onDragStart={(event: ReactDragEvent<HTMLTableCellElement>) => {
                    draggedColumnId.current = String(column.columnId);
                    event.dataTransfer.effectAllowed = "move";
                    event.dataTransfer.setData("text/plain", String(column.columnId));
                  }}
                  onDragOver={(event: ReactDragEvent<HTMLTableCellElement>) => event.preventDefault()}
                  onDrop={(event: ReactDragEvent<HTMLTableCellElement>) => {
                    event.preventDefault();
                    const sourceId = draggedColumnId.current ?? event.dataTransfer.getData("text/plain");
                    if (sourceId) moveColumn(sourceId, String(column.columnId));
                    draggedColumnId.current = null;
                  }}
                  onDragEnd={() => { draggedColumnId.current = null; }}
                  onKeyDown={(event: ReactKeyboardEvent<HTMLTableCellElement>) => {
                    if (!event.altKey || (event.key !== "ArrowLeft" && event.key !== "ArrowRight")) return;
                    event.preventDefault();
                    moveColumnByOffset(String(column.columnId), event.key === "ArrowLeft" ? -1 : 1);
                  }}
                  title="Drag to reorder; press Alt+Left or Alt+Right to move"
                >{column.renderHeaderCell()}</DataGridHeaderCell>
              )}
            </DataGridRow>
          </DataGridHeader>
          <DataGridBody<DebugSnapshot>>
            {({ item, rowId }) => (
              <DataGridRow
                key={rowId}
                className={`snapshot-grid-row${isSelectedSnapshot(item, selectedSnapshot) ? " selected" : ""}`}
              >
                {(column) => (
                  <DataGridCell
                    key={column.columnId}
                    className="snapshot-grid-cell"
                    focusMode={
                      column.columnId === "lineage" ||
                        column.columnId === "payload-json" ||
                        String(column.columnId).startsWith("payload:")
                        ? "group"
                        : "none"
                    }
                  >{column.renderCell(item)}</DataGridCell>
                )}
              </DataGridRow>
            )}
          </DataGridBody>
        </DataGrid>
        {filteredItems.length === 0 && <div className="inline-empty grid-empty">No rows match the active column filters.</div>}
        {onLoadMore && hasMoreRows && <div className="snapshot-grid-sentinel" ref={loadMoreSentinelRef} aria-hidden="true" />}
        {onLoadMore && loadingRows && <div className="snapshot-grid-loading" role="status">Loading more rows…</div>}
      </div>
    </div>
  );
}
