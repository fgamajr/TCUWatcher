#!/bin/sh

# --- Configuration: Directories to Exclude ---
# Add or remove directory names, separated by |
# Example: "bin|obj|.vs|packages|.git|node_modules"
# Ensure no leading/trailing spaces around names or pipes for this version.
EXCLUDE_DIRS_NAMES="bin|obj|.vs|packages|.git|node_modules"

# --- Prepare find command parts for exclusion ---
_EXCLUDE_PATTERNS_LIST=""
if [ -n "$EXCLUDE_DIRS_NAMES" ]; then
    OLD_IFS="$IFS"
    IFS="|"
    for dir_name in $EXCLUDE_DIRS_NAMES; do
        if [ -n "$dir_name" ]; then # Ensure dir_name is not empty after split
            if [ -z "$_EXCLUDE_PATTERNS_LIST" ]; then
                _EXCLUDE_PATTERNS_LIST="-name $dir_name"
            else
                _EXCLUDE_PATTERNS_LIST="$_EXCLUDE_PATTERNS_LIST -o -name $dir_name"
            fi
        fi
    done
    IFS="$OLD_IFS"
fi

PRUNE_CLAUSE_FOR_FIND=""
if [ -n "$_EXCLUDE_PATTERNS_LIST" ]; then
    # Construct the clause like: \( -name bin -o -name obj \) -prune -o
    # The \( and \) must be separate arguments to find, hence no quotes around them when $PRUNE_CLAUSE_FOR_FIND is expanded.
    # Similarly, $_EXCLUDE_PATTERNS_LIST will expand to multiple arguments.
    # Using an array would be safer here if the shell supported it (e.g. bash),
    # but for /bin/sh, careful expansion is key.
    # We will build the arguments for find strategically.
    # For now, let's make PRUNE_CLAUSE_FOR_FIND an array of arguments if we can,
    # or ensure it's correctly expanded. Simpler: construct parts and use them.
    # This structure is: (PATTERN_LIST) -prune -o
    # So if PATTERN_LIST is empty, this whole clause is skipped.
    # find . \( -name foo -o -name bar \) -prune -o -print
    # find . \( -name foo -o -name bar \) -prune -o -type f -print -exec ...
    # The \(, \), and -o need to be passed as distinct arguments.
    # PRUNE_CLAUSE_FOR_FIND will be used like: find . ${PRUNE_CLAUSE_FOR_FIND} باقی_آرگومان_ها
    # For /bin/sh, we can't use arrays directly in a POSIX way for this.
    # So we construct the arguments and let word splitting do its job, carefully.
    # We will pass arguments to a helper function or use eval carefully if it gets too complex.
    # For now, let's try direct expansion.
    # The structure will be: find . \( list of -name foo -o -name bar \) -prune -o remaining_expression
    # We will use set -- to build arguments for find if it gets tricky.
    true # placeholder for better construction if needed below
else
    true # No exclusion patterns
fi


# --- Display Folder Structure ---
echo "##################################################"
echo "##      FOLDER STRUCTURE (with exclusions)      ##"
echo "##################################################"
if [ -n "$EXCLUDE_DIRS_NAMES" ]; then
    echo "[INFO] Attempting to exclude directories: $EXCLUDE_DIRS_NAMES"
else
    echo "[INFO] No directories specified for exclusion in structure."
fi
echo ""

if command -v tree >/dev/null 2>&1; then
  echo "[INFO] Using 'tree' command."
  if [ -n "$EXCLUDE_DIRS_NAMES" ]; then
    tree -I "$EXCLUDE_DIRS_NAMES"
  else
    tree
  fi
else
  echo "[INFO] 'tree' command not found. Displaying directory list using 'find'."
  # Build find arguments
  find_args_structure="."
  if [ -n "$_EXCLUDE_PATTERNS_LIST" ]; then
    # Note: Using eval here is generally risky, but find's syntax with dynamic parentheses is tricky.
    # A safer approach would be to build an arg list if the shell supports it (like bash arrays).
    # For POSIX sh, this becomes more verbose or requires helper functions.
    # Let's try to avoid eval first by constructing a string of arguments
    # that rely on shell word splitting.
    # The core issue is passing \( and \) correctly.
    # find . \( -name foo -o -name bar \) -prune -o -print
    # We will construct arguments to find and execute.
    # Using `set --` to build arguments for `find`
    set -- .
    if [ -n "$_EXCLUDE_PATTERNS_LIST" ]; then
        set -- "$@" \( $_EXCLUDE_PATTERNS_LIST \) -prune -o
    fi
    set -- "$@" -print
    find "$@"
  else
    find . -print
  fi
fi

echo ""
echo "##################################################"
echo "##   FILE CONTENTS (RECURSIVE, with exclusions) ##"
echo "##################################################"
if [ -n "$EXCLUDE_DIRS_NAMES" ]; then
    echo "[INFO] Attempting to exclude files within: $EXCLUDE_DIRS_NAMES"
else
    echo "[INFO] No directories specified for exclusion for file contents."
fi
echo "[INFO] Displaying content only for specific text-based MIME types."
echo ""

# Build find arguments for file contents
set -- .
if [ -n "$_EXCLUDE_PATTERNS_LIST" ]; then
    # This will expand $_EXCLUDE_PATTERNS_LIST into its component arguments like -name foo -o -name bar
    set -- "$@" \( $_EXCLUDE_PATTERNS_LIST \) -prune -o
fi
set -- "$@" -type f -print -exec sh -c '
    filepath="$1"
    echo ""
    echo "=========================================================="
    echo "FILE: ${filepath}"
    echo "----------------------------------------------------------"
    mimetype=$(file -b --mime-type "${filepath}")

    if echo "${mimetype}" | grep -q -E "^text/|^application/json$|^application/xml$|^application/javascript$|^application/xhtml\+xml$"; then
        cat "${filepath}"
    else
        echo "[INFO] Non-displayable content type or binary file."
        echo "       MIME type: ${mimetype}"
    fi
    echo "----------------------------------------------------------"
    echo "END OF FILE: ${filepath}"
    echo "=========================================================="
    echo ""
' sh {} \;

find "$@"


echo "##################################################"
echo "##                 SCRIPT END                   ##"
echo "##################################################"