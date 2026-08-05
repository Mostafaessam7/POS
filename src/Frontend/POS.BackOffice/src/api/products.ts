import { useEffect, useState } from "react";
import { ApiError, apiGet } from "./client";

export interface Product {
  id: string;
  name: string;
  variantId: string;
}

interface ProductListResponse {
  items: Product[];
}

/** The tenant's products, for populating a variant picker instead of typing a GUID by hand. */
export function useProducts() {
  const [products, setProducts] = useState<Product[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    apiGet<ProductListResponse>("/api/v1/catalog/products")
      .then((response) => setProducts(response.items))
      .catch((err) => setError(err instanceof ApiError ? err.message : "Failed to load products."));
  }, []);

  return { products, error };
}
